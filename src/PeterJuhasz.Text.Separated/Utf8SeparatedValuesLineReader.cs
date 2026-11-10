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

	private const int StackAllocThreshold = 256;

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

		// utf-8 never decodes to more chars than bytes
		var maxLength = (int)slot.Length;
		char[]? rented = null;
		var buffer = maxLength <= StackAllocThreshold
			? stackalloc char[StackAllocThreshold]
			: (rented = ArrayPool<char>.Shared.Rent(maxLength));
		try
		{
			if (!TryDecodeSlot(slot, buffer, out var charsWritten))
			{
				value = default;
				return false;
			}

			value = new string(buffer[..charsWritten]);
			return true;
		}
		finally
		{
			if (rented is not null)
			{
				ArrayPool<char>.Shared.Return(rented);
			}
		}
	}

	/// <summary>
	/// Reads the next value into <paramref name="destination"/>, unquoted and unescaped.
	/// </summary>
	/// <returns><see langword="false"/> if there are no more values, or if <paramref name="destination"/> is too small. The value is consumed in both cases.</returns>
	public bool TryReadString(scoped Span<char> destination, out int charsWritten)
	{
		if (!TryReadSlot(out var slot))
		{
			charsWritten = 0;
			return false;
		}

		return TryDecodeSlot(slot, destination, out charsWritten);
	}

	private readonly bool TryDecodeSlot(ReadOnlySequence<byte> slot, scoped Span<char> destination, out int charsWritten)
	{
		if (slot.IsEmpty)
		{
			charsWritten = 0;
			return true;
		}

		// fast path: contiguous and unquoted, decode directly
		if (slot.IsSingleSegment && slot.FirstSpan[0] != (byte)_options.Quote)
		{
			return Encoding.UTF8.TryGetChars(slot.FirstSpan, destination, out charsWritten);
		}

		// copy to contiguous buffer and unquote in place
		var length = (int)slot.Length;
		byte[]? rented = null;
		var bytes = length <= StackAllocThreshold
			? stackalloc byte[StackAllocThreshold]
			: (rented = ArrayPool<byte>.Shared.Rent(length));
		try
		{
			bytes = bytes[..length];
			slot.CopyTo(bytes);
			Unquote(bytes, out length);
			return Encoding.UTF8.TryGetChars(bytes[..length], destination, out charsWritten);
		}
		finally
		{
			if (rented is not null)
			{
				ArrayPool<byte>.Shared.Return(rented);
			}
		}
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