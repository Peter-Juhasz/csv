# CSV writer
High-performance CSV writer using System.Text.Json as a model.

## Rules
Outside the simple rules like:
 - each value must be separated by a comma (or tab, or some other separator)
 - each row must be separated by a line

There are some rules for encoding special values:
 - if the value contains a meta character which has a meaning in the CSV syntax (a value or row separator),
   the value must be enclosed in double quotes, e.g.: `"this, or that"`
 - if the value contains a double quotes, it must be doubled, e.g.: `"some ""special"" value"`

## Base implementation
Let's start with the shortest, probably the most straightforward implementation.

The `Encode` function could look like this, we encode in all cases for the sake of simplicity:
```cs
static string? Encode(string? value) =>
	'"' + value?.Replace("\"", "\"\"") + '"';
```

And rendering the content would use simple query operators:
```cs
static string ToCsv<T>(this IEnumerable<T> items, char separator = ',')
{
	var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
	var rows =
		from row in items
		select String.Join(separator, 
			from property in properties
			select Encode(property.GetValue(row))
		)
	;
	var csv = String.Join(Environment.NewLine, rows);
	return csv;
}
```

But this implementation is the worst in terms of memory usage:
 - each string operation (adding a quote, replacing something, concatenating values and then lines) consumes a tremendous amount of memory, because each operation stores the result at a new memory location
 - values and rows has to be either enumerated multiple times, or buffered into memory
 - encoding a value encodes even if it won't be necessary

## Optimize: encoding
We could start by optimizing encoding, and encode only if it is necessary:

```cs
static char[] ToEscape = new [] { ',', '\n', '\r' };

static string? Encode(string? value) => value switch
{
	// null translates to an empty string
    null => String.Empty,

	// worst case, we need to add quotes and replace double quotes as well
    _ when value.Contains('"') => String.Concat('"', value.Replace("\"", "\"\""), '"'),

	// we don't need to replace, but must add quotes
    _ when value.IndexOfAny(ToEscape) is not -1 => String.Concat('"', value, '"'),

	// no escaping needed
    _ => value
};
```

This way we can eliminate a lots of unnecessary string operations and thus save lots of memory,
because we transform the value only if it is necessary, and in all other cases which are the most likely,
we just let it flow through.

## Optimize: concatenation
Our next target is the following:
```cs
var line = String.Concat(',', properties.Select(p => Encode(p.GetValue(row))));
```

We could create a `CsvWriter` like [XmlWriter](https://docs.microsoft.com/en-us/dotnet/api/system.xml.xmlwriter) or [Utf8JsonWriter](https://docs.microsoft.com/en-us/dotnet/api/system.text.json.utf8jsonwriter),
to capture the semantics of CSV, and decrease the complexity of concatenation as well.

The semantics would be the following:
 - write a value (and manage separators)
 - write a line

At first, let's start from a [TextWriter](https://docs.microsoft.com/en-us/dotnet/api/system.io.textwriter):
```cs
public class CsvWriter : IDisposable
{
    public CsvWriter(TextWriter writer, CsvEncoder encoder)
    {
        Writer = writer;
        Encoder = encoder;
    }

    public TextWriter Writer { get; }
    public CsvEncoder Encoder { get; }

    private bool shouldAppendSeparator = false;

    private void EnsureSeparator()
    {
        if (shouldAppendSeparator)
        {
            Writer.Write(Encoder.Separator);
        }
    }

    public void WriteValue(string? value)
    {
        EnsureSeparator();
        Writer.Write(Encoder.Encode(value));
        shouldAppendSeparator = true;
    }

    public void WriteLine()
    {
        Writer.WriteLine();
        shouldAppendSeparator = false;
    }

    public async Task FlushAsync() => await Writer.FlushAsync();

    public void Dispose() => ((IDisposable)Writer).Dispose();
}
```

An example usage would look like this:
```cs
using var buffer = new MemoryStream();
using var streamWriter = new StreamWriter(buffer);
using var writer = new CsvWriter(streamWriter);

// write header
writer.WriteValue("A");
writer.WriteValue("B");
writer.WriteValue("C");
writer.WriteLine();

// write values
writer.WriteValue("1");
writer.WriteValue("2");
writer.WriteValue("3");
writer.WriteLine();

await writer.FlushAsync();
```

Notice how easy it becomes as our `CsvWriter` takes care of the separators between each value, and the complixity of managing that state is captured.

And also, there are no more concatenations anymore, finally, we write values one after the other right into a `Stream`.

## Optimize: formatting
Our next area of focus is formatting. Usually we don't write only `String`s, but numbers, dates, sometimes even `Guid`s. At the current stage, we have no other option than formating these values to a `String` and then pass the `String` to `WriteValue`:
```cs
writer.WriteValue(2.ToString()); // int
writer.WriteValue(Guid.NewGuid().ToString()); // guid
writer.WriteValue(DateTimeOffset.Now.ToString()); // date time
```

This can be really wasteful, as the `string` we create exists only temporarily for the time of writing. But since it is a reference type, it would be circulating in the Garbage Collector for some time.

Fortunately, the `TextWriter` supports not only `Write(string)` but [Write(ReadOnlySpan<char>](https://docs.microsoft.com/en-us/dotnet/api/system.io.textwriter.write?#System_IO_TextWriter_Write_System_ReadOnlySpan_System_Char__) as well. Which means, we don't have to create a `String` every time, but we could reuse a `char` buffer for formatting:
```cs
private char[] FormatBuffer = new char[128];

public void WriteValue(Guid value)
{
    const int length = 36;
    EnsureSeparator();
    value.TryFormat(FormatBuffer, out _);
    Writer.Write(FormatBuffer.AsSpan(..length));
    shouldAppendSeparator = true;
}

public void WriteValue(int value)
{
    EnsureSeparator();
    value.TryFormat(FormatBuffer, out var length);
    Writer.Write(FormatBuffer.AsSpan(..length));
    shouldAppendSeparator = true;
}
```

Introducing a buffer at instance level would break thread-safety, but `TextWriter` is not safe anyway, locking is the responsibility of the user.
But with this improvement, we don't create any short-lived, temporal `String`s, but reuse the same buffer for each formatting.

## Optimize: buffer management
TODO

## Optimize: reflection
TODO
