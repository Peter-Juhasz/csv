using System.Buffers;
using System.Buffers.Text;
using System.Text;

namespace PeterJuhasz.Text.Separated;

public ref struct Utf8SeparatedValuesLineReader
{
	public Utf8SeparatedValuesLineReader(ReadOnlySequence<byte> sequence, SeparatedValuesReaderOptions options, State state = State.Initial)
	{
		_remaining = sequence;
		_options = options;
		_state = state;
	}

	private ReadOnlySequence<byte> _remaining;
	private readonly SeparatedValuesReaderOptions _options;
	private State _state;

	public bool TryReadString(out string? value)
	{
		if (!TryReadSlot(out var slot))
		{
			value = default;
			return false;
		}

		if (slot.IsEmpty)
		{
			value = string.Empty;
			return true;
		}

		if (slot.FirstSpan[0] == (byte)_options.Quote)
		{
			var length = (int)slot.Length;
			Span<byte> span = stackalloc byte[length];
			slot.CopyTo(span);
			Unquote(span, out length);
			value = Encoding.UTF8.GetString(span[..length]);
			return true;
		}

		value = Encoding.UTF8.GetString(slot);
		return true;
	}

	public bool TryReadInt32(out int value)
	{
		if (!TryReadSlot(out var slot))
		{
			value = default;
			return false;
		}

		var length = (int)slot.Length;
		Span<byte> span = stackalloc byte[length];
		slot.CopyTo(span);
		Unquote(span, out length);
		if (!Utf8Parser.TryParse(span[..length], out value, out _))
		{
			return false;
		}

		return true;
	}

	public bool TryReadInt64(out long value)
	{
		if (!TryReadSlot(out var slot))
		{
			value = default;
			return false;
		}

		var length = (int)slot.Length;
		Span<byte> span = stackalloc byte[length];
		slot.CopyTo(span);
		Unquote(span, out length);
		if (!Utf8Parser.TryParse(span[..length], out value, out _))
		{
			return false;
		}

		return true;
	}

	private readonly bool Unquote(Span<byte> bytes, out int written)
	{
		if (bytes.Length > 1 && bytes[0] == (byte)_options.Quote && bytes[^1] == (byte)_options.Quote)
		{
			// remove trailing quotes
			var inner = bytes[1..^1];
			inner.CopyTo(bytes);
			var remainingLength = bytes.Length - 2;
			var remaining = bytes[..remainingLength];

			// unescape quotes
			ReadOnlySpan<byte> escapedQuote = [(byte)_options.Quote, (byte)_options.Quote];
			var index = remaining.IndexOf(escapedQuote);
			while (index != -1)
			{
				remaining[(index + 1)..].CopyTo(remaining[index..]);
				remainingLength -= 1;
				remaining = bytes[..remainingLength];
				index = remaining.IndexOf(escapedQuote);
			}

			written = remainingLength;
			return true;
		}

		written = bytes.Length;
		return false;
	}

	private bool TryReadSlot(out ReadOnlySequence<byte> slot)
	{
		if (_state is State.EndOfLine or State.Invalid)
		{
			slot = default;
			return false;
		}

		// read next
		var reader = new SequenceReader<byte>(_remaining);
		if (!reader.TryPeek(out var peeked))
		{
			// end of line
			_state = State.EndOfLine;
			slot = default;
			return false;
		}

		// skip delimiter after previous value
		var skippedDelimiter = false;
		if (_state is State.ReadValue && peeked == (byte)_options.Delimiter)
		{
			reader.Advance(1);
			skippedDelimiter = true;

			// end of line
			if (!reader.TryPeek(out peeked))
			{
				_state = State.EndOfLine;
				slot = default;
				return false;
			}
		}

		// end of line
		if (!skippedDelimiter && peeked is (byte)'\n' or (byte)'\r')
		{
			_state = State.EndOfLine;
			slot = default;
			return false;
		}

		// read quoted
		if (peeked == (byte)_options.Quote)
		{
			var unread = reader.UnreadSequence;
			reader.Advance(1);
			if (!reader.TryReadTo(out slot, (byte)_options.Quote, advancePastDelimiter: true))
			{
				_state = State.Invalid;
				return false;
			}

			// read escaped quotes
			while (reader.IsNext((byte)_options.Quote, advancePast: true))
			{
				if (!reader.TryReadTo(out ReadOnlySequence<byte> part, (byte)_options.Quote, advancePastDelimiter: true))
				{
					_state = State.Invalid;
					return false;
				}
				slot = unread.Slice(0, reader.Position);
			}

			_state = State.ReadValue;
			_remaining = reader.UnreadSequence;
			return true;
		}

		// read until next delimiter
		if (reader.TryReadTo(out slot, (byte)_options.Delimiter, advancePastDelimiter: false))
		{
			_state = State.ReadValue;
			_remaining = reader.UnreadSequence;
			return true;
		}

		// read until end of line
		else if (reader.TryReadToAny(out slot, "\r\n"u8, advancePastDelimiter: false))
		{
			_state = State.EndOfLine;
			_remaining = reader.UnreadSequence;
			return true;
		}

		// read remaining
		else if (reader.UnreadSequence.Length > 0)
		{
			_state = State.EndOfLine;
			slot = reader.UnreadSequence;
			return true;
		}

		_state = State.Invalid;
		return false;
	}

	public enum State
	{
		Initial,
		ReadValue,
		EndOfLine,
		Invalid,
	}
}