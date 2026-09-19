using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace System.Text.Separated;

[RequiresUnreferencedCode("Uses reflection to create generic types at runtime.")]
public class SeparatedValuesReader<T>(SeparatedValuesReaderOptions options, Func<object[], T>? factory = null)
{
	public async IAsyncEnumerable<T> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		var reader = PipeReader.Create(stream);
		await foreach (var item in ReadAsync(reader, cancellationToken))
		{
			yield return item;
		}
	}

	public async IAsyncEnumerable<T> ReadAsync(PipeReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		// skip utf 8 bom
		var result = await reader.ReadAtLeastAsync(3, cancellationToken);
		if (result.Buffer.Length >= 3)
		{
			var slice = result.Buffer.Slice(0, 3);
			Span<byte> span = stackalloc byte[3];
			slice.CopyTo(span);
			if (span is [0xEF, 0xBB, 0xBF])
			{
				reader.AdvanceTo(slice.End, slice.End);
			}
		}

		// read header
		ParameterIndex[] parameterIndices;
		var parameters = ParametersCache<T>.Parameters;
		List<string>? headerNamesList = null;
		if (options.HasHeader)
		{
			var headerLine = await reader.ReadLineAsync(maximumLength: 4096, cancellationToken);
			if (headerLine.IsEmpty)
			{
				yield break;
			}

			var headerReader = new Utf8SeparatedValuesLineReader(headerLine, options);
			using var workingParameterIndices = new PooledArrayBuilder<ParameterIndex>();
			while (headerReader.TryReadString(out var headerName))
			{
				if (headerName.IsNullOrWhiteSpace())
				{
					throw new Exception("Empty header");
				}

				if (options.UnknownColumns is SeparatedValuesReaderOptions.UnknownColumnHandling.LoadIntoExtraValues)
				{
					(headerNamesList ??= []).Add(headerName);
				}

				// find parameter
				var parameterIndex = parameters.IndexOf(p => p.Name!.Equals(headerName, StringComparison.OrdinalIgnoreCase));
				if (parameterIndex == -1)
				{
					switch (options.UnknownColumns)
					{
						case SeparatedValuesReaderOptions.UnknownColumnHandling.Ignore:
							workingParameterIndices.Add(new(null, parameterIndex));
							break;

						case SeparatedValuesReaderOptions.UnknownColumnHandling.LoadIntoExtraValues:
							workingParameterIndices.Add(new(null, parameterIndex));
							break;

						case SeparatedValuesReaderOptions.UnknownColumnHandling.Throw:
							throw new InvalidOperationException($"Header '{headerName}' not found.");
					}
					continue;
				}

				var parameter = parameters[parameterIndex];
				if (workingParameterIndices.Any(p => p.Parameter?.Name == parameter.Name))
				{
					throw new InvalidOperationException($"Duplicate header '{headerName}'");
				}

				// add
				workingParameterIndices.Add(new(parameter, parameterIndex));
			}
			reader.AdvanceTo(headerLine.End, headerLine.End);

			parameterIndices = workingParameterIndices.ToArray(); // devirtualize
		}
		else
		{
			parameterIndices = new ParameterIndex[parameters.Length];
			for (var i = 0; i < parameters.Length; i++)
			{
				parameterIndices[i] = new(parameters[i], i);
			}
		}

		// process records
		var values = new object?[parameters.Length];
		var headerNames = headerNamesList?.ToArray();
		while (true)
		{
			var line = await reader.ReadLineAsync(maximumLength: 4096, cancellationToken);
			if (line.IsEmpty)
			{
				break;
			}

			// Skip empty lines (lines that contain only whitespace or line breaks)
			if (IsEmptyLine(line))
			{
				reader.AdvanceTo(line.End, line.End);
				continue;
			}

			values.AsSpan().Clear();
			var lineReader = new Utf8SeparatedValuesLineReader(line, options);
			SmallDictionary<string, string?>? extraValues = null;
			for (int i = 0; i < parameterIndices.Length; i++)
			{
				var mapping = parameterIndices[i];
				if (mapping.Parameter == null)
				{
					switch (options.UnknownColumns)
					{
						case SeparatedValuesReaderOptions.UnknownColumnHandling.Ignore:
							lineReader.TryReadString(out _);
							break;

						case SeparatedValuesReaderOptions.UnknownColumnHandling.LoadIntoExtraValues:
							extraValues ??= new();
							if (!lineReader.TryReadString(out var extraValue))
							{
								throw new InvalidOperationException("Error reading extra value.");
							}
							if (!extraValue.IsNullOrEmpty())
							{
								var headerName = headerNamesList![i];
								extraValues[headerName] = extraValue;
							}
							break;

						case SeparatedValuesReaderOptions.UnknownColumnHandling.Throw:
							throw new InvalidOperationException("This should not happen.");
					}
					continue;
				}

				var parameterType = mapping.Parameter.ParameterType;
				if (parameterType == typeof(string))
				{
					if (!lineReader.TryReadString(out var value))
					{
						throw new InvalidOperationException($"Error reading value of type {mapping.Parameter.ParameterType}.");
					}

					values[mapping.Index] = value;
				}
				else if (parameterType == typeof(int))
				{
					if (!lineReader.TryReadInt32(out var value))
					{
						throw new InvalidOperationException($"Error reading value of type {mapping.Parameter.ParameterType}.");
					}

					values[mapping.Index] = value;
				}
				else if (parameterType == typeof(long))
				{
					if (!lineReader.TryReadInt64(out var value))
					{
						throw new InvalidOperationException($"Error reading value of type {mapping.Parameter.ParameterType}.");
					}

					values[mapping.Index] = value;
				}
				else if (parameterType == typeof(DateTimeOffset))
				{
					if (!lineReader.TryReadString(out var valueStr) || !valueStr.TryParseAs<DateTimeOffset>(out var value))
					{
						throw new InvalidOperationException($"Error reading value of type {mapping.Parameter.ParameterType}.");
					}

					values[mapping.Index] = value;
				}
				else
				{
					throw new NotSupportedException($"Unsupported parameter type '{mapping.Parameter.ParameterType}'");
				}
			}
			reader.AdvanceTo(line.End, line.End);

			if (options.UnknownColumns is SeparatedValuesReaderOptions.UnknownColumnHandling.LoadIntoExtraValues)
			{
				values[^1] = extraValues;
			}

			yield return factory != null ? factory(values!) : (T)Activator.CreateInstance(typeof(T), args: values)!;
		}
	}

	private readonly record struct ParameterIndex(ParameterInfo? Parameter, int Index);

	private static bool IsEmptyLine(ReadOnlySequence<byte> line)
	{
		foreach (var segment in line)
		{
			foreach (var b in segment.Span)
			{
				if (b is not ((byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t'))
				{
					return false;
				}
			}
		}
		return true;
	}

#pragma warning disable CS0693
	[RequiresUnreferencedCode("Uses reflection to create generic types at runtime.")]
	private static class ParametersCache<T>
	{
		public static readonly ParameterInfo[] Parameters = typeof(T).GetConstructors(BindingFlags.Public | BindingFlags.Instance)[0].GetParameters();
	}
}
