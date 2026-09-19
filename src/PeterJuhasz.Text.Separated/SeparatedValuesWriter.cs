using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Reflection;

namespace System.Text.Separated;

[RequiresUnreferencedCode("Uses reflection to create generic types at runtime.")]
public class SeparatedValuesWriter(PipeWriter writer, SeparatedValuesWriterOptions options)
{
	private static readonly ReadOnlyMemory<byte> CrLf = Encoding.UTF8.GetBytes("\r\n");
	private static readonly ReadOnlyMemory<byte> Lf = Encoding.UTF8.GetBytes("\n");

	private readonly SearchValues<char> DelimiterSearchValues = SearchValues.Create([options.Delimiter, options.Quote, .. options.NewLine]);

	private readonly Encoding Encoding = Encoding.UTF8;

	private readonly ReadOnlyMemory<byte> NewLine = options.NewLine switch
	{
		"\r\n" => CrLf,
		"\n" => Lf,
		_ => Encoding.UTF8.GetBytes(options.NewLine)
	};

	private State _state = State.AfterLine;

	private void EnsureDelimiter()
	{
		if (_state == State.AfterValue)
		{
			var span = writer.GetSpan(1);
			span[0] = (byte)options.Delimiter;
			writer.Advance(1);
		}
	}

	public void WriteHeader(ReadOnlySpan<char> header)
	{
		if (!options.HasHeader)
		{
			return;
		}

		WriteValue(header);
	}

	public void WriteValue(ReadOnlySpan<char> chars)
	{
		EnsureDelimiter();

		var needsQuotes = chars.ContainsAny(DelimiterSearchValues);

		if (needsQuotes)
		{
			var span = writer.GetSpan(1);
			span[0] = (byte)options.Quote;
			writer.Advance(1);
		}

		var processedLength = 0;
		foreach (var index in chars.IndexesOf(options.Quote))
		{
			var slice = chars[processedLength..index];
			var buffer = writer.GetSpan(Encoding.GetByteCount(slice) + 2);
			var writtenBytes = Encoding.GetBytes(slice, buffer);
			buffer[writtenBytes] = (byte)options.Quote;
			buffer[writtenBytes + 1] = (byte)options.Quote;
			writer.Advance(writtenBytes + 2);
			processedLength = index + 1;
		}

		if (processedLength < chars.Length)
		{
			var slice = chars[processedLength..];
			var buffer = writer.GetSpan(Encoding.GetByteCount(slice));
			var writtenBytes = Encoding.GetBytes(slice, buffer);
			writer.Advance(writtenBytes);
		}

		if (needsQuotes)
		{
			var span = writer.GetSpan(1);
			span[0] = (byte)options.Quote;
			writer.Advance(1);
		}

		_state = State.AfterValue;
	}

	public void WriteValue(string? value)
	{
		if (value == null)
		{
			EnsureDelimiter();
			_state = State.AfterValue;
			return;
		}

		WriteValue(value.AsSpan());
	}

	public void WriteValue(bool value) => WriteValue(value switch
	{
		true => "true",
		false => "false"
	});

	public void WriteValue(int value) => WriteUtf8FormattableValue(value, default);

	public void WriteValue(long value) => WriteUtf8FormattableValue(value, default);

	public void WriteValue(DateTimeOffset value)
	{
		if (value is { Millisecond: 0, Offset: { Hours: 0, Minutes: 0 } })
		{
			WriteUtf8FormattableValue(value, "yyyy-MM-ddTHH:mm:ss");
			return;
		}

		WriteUtf8FormattableValue(value, "O");
	}

	public void WriteEmptyValue()
	{
		EnsureDelimiter();
		_state = State.AfterValue;
	}

	public void WriteUtf8FormattableValue<T>(T value, ReadOnlySpan<char> format, IFormatProvider? provider = null) where T : IUtf8SpanFormattable
	{
		EnsureDelimiter();
		var span = writer.GetSpan(1 + 64 + 1);
		span[0] = (byte)options.Quote;
		var result = value.TryFormat(span[1..], out var written, format, provider);
		if (!result)
		{
			throw new InvalidOperationException("Failed to format value.");
		}
		span[1 + written] = (byte)options.Quote;
		writer.Advance(1 + written + 1);
		_state = State.AfterValue;
	}

	public void WriteUtf8FormattableValue<T>(Nullable<T> value, ReadOnlySpan<char> format, IFormatProvider? provider = null) where T : struct, IUtf8SpanFormattable
	{
		if (!value.HasValue)
		{
			WriteEmptyValue();
			return;
		}

		WriteUtf8FormattableValue(value.Value, format, provider);
	}

	public void WriteFormattableValue<T>(T value, ReadOnlySpan<char> format, IFormatProvider? provider = null) where T : ISpanFormattable
	{
		Span<char> span = stackalloc char[64];
		var result = value.TryFormat(span, out var written, format, provider);
		if (!result)
		{
			throw new InvalidOperationException("Failed to format value.");
		}
		WriteValue(span[..written]);
	}

	public void WriteFormattableValue<T>(Nullable<T> value, ReadOnlySpan<char> format, IFormatProvider? provider = null) where T : struct, ISpanFormattable
	{
		if (!value.HasValue)
		{
			WriteEmptyValue();
			return;
		}

		WriteFormattableValue(value.Value, format, provider);
	}


	public void WriteLine()
	{
		writer.Write(NewLine.Span);
		_state = State.AfterLine;
	}

	public void WriteHeader<T>()
	{
		if (_cachedProperties == null)
		{
			var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
			_cachedProperties = properties;
		}

		foreach (var property in _cachedProperties)
		{
			WriteHeader(property.Name);
		}
		WriteLine();
	}

	private PropertyInfo[]? _cachedProperties;

	public void WriteValues<T>(T value)
	{
		if (_cachedProperties == null)
		{
			var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
			_cachedProperties = properties;
		}

		foreach (var property in _cachedProperties)
		{
			var propertyValue = property.GetValue(value);
			switch (propertyValue)
			{
				case bool b:
					WriteValue(b);
					break;

				case int i:
					WriteValue(i);
					break;

				case long l:
					WriteValue(l);
					break;

				case DateTimeOffset l:
					WriteValue(l);
					break;

				default:
					WriteValue(propertyValue?.ToString());
					break;
			}
		}
		WriteLine();
	}

	public ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken) => writer.FlushAsync(cancellationToken);

	private enum State
	{
		AfterLine,
		AfterValue,
	}
}
