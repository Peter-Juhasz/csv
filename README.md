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
static readonly char[] ToEscape = new [] { ',', '\n', '\r' };

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

This can be really wasteful, as the `string` we create exists only temporarily for the time of writing. But since it is a reference type, it would be circulating in the Garbage Collector for some time. Rendering a few hundreds or thousands of lines of CSV may put significant pressure on it.

Fortunately, the `TextWriter` supports not only `Write(string)` but [Write(ReadOnlySpan&lt;char&gt;)](https://docs.microsoft.com/en-us/dotnet/api/system.io.textwriter.write?#System_IO_TextWriter_Write_System_ReadOnlySpan_System_Char__) as well. Which means, we don't have to create a `String` every time, but we could reuse a `char` buffer for formatting:
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

We might have some other choices like:
 - Use an `ArrayPool<char>` to rent and reuse arrays, but since we don't have to support thread-safety, a dedicated buffer could be a better choice. Or, rent that buffer only once per instance.
 - Use `stackalloc` and allocate a new array each time, but we could also reuse that buffer across invocations, so this is what we did by moving that buffer to instance level.

## Optimize: buffer management and binary representation
We made lots of improvements to the base implementation, but there is still a lot to go. We used the basic `Stream` and `StreamWriter` concept earlier, but betters concepts and tools appeared over the years. These concepts were replaced by [PipeWriter](https://docs.microsoft.com/en-us/dotnet/api/system.io.pipelines.pipewriter) which simplifies buffer management and thus provides more efficient operation.

On the other hand, we want to go a level deeper. In the previous implementation, our unit was a `char` which is still not the raw binary data, since a single `char` can easily take up multiple `byte`s to represent. So, our new implementation has two dependencies, the underlying `PipeWriter` (which replaces the `TextWriter`), and an `Encoding` to be able to translate `char`s to `byte`s:

```cs
public CsvPipeWriter(PipeWriter writer, Encoding encoding)
{
    // ...
}
```

The concept of Pipelines, is different than `Stream`s, they feel like they are turned inside out. Because, we don't &quot;write&quot;/&quot;send&quot; data, but we request and get a buffer from the `PipeWriter` where we can write some data.

Let's take the following example, where we would like to write 1 byte:
1. First, we request a buffer which is at least 1 byte long.
2. We copy our data to the buffer.
3. We signal that we wrote 1 byte.

```cs
private void WriteSeparator()
{
    var span = Writer.GetSpan(1);
    span[0] = (byte)',';
    Writer.Advance(1);
}
```

Let's rewrite the earlier examples to use the `PipeWriter`, and also to translate to two layers deep:

In case of `Guid`, it is easy, because we know exactly that a `Guid` is always represented by exactly 36 `char`s, no more. We also konw that these are simple letters and digits and hyphens, so basic ASCII encoding can be used, where each `char` is represented by a single `byte`:
```cs
public void WriteValue(Guid value)
{
    const int Length = 36;
    EnsureSeparator();
    value.TryFormat(formatBuffer, out _);
    var span = Writer.GetSpan(Length);
    Encoding.ASCII.GetBytes(formatBuffer.AsSpan(..Length), span);
    Writer.Advance(Length);
    shouldAppendSeparator = true;
}
```

In case of `int`, we don't know how many characters we are going to need (we could surely do some math to calculate it), but we also konw that the result contains only digits, dot, comma, space, negative sign, so basic ASCII encoding can be used, where each `char` is represented by a single `byte`:
```cs
public void WriteValue(int value)
{
    EnsureSeparator();
    value.TryFormat(formatBuffer, out var length);
    var span = Writer.GetSpan(length);
    Encoding.ASCII.GetBytes(formatBuffer.AsSpan(..length), span);
    Writer.Advance(length);
    shouldAppendSeparator = true;
}
```

In case of a `string`, we don't know how many bytes we are going to need, because it depends on the encoding. But fortunately, we can ask the `Encoding` (more specifically the `Encoder`) about how many `byte`s are we going to need. The following example is simplified, it doesn't take CSV escaping (double quotes) into account.
public void WriteValue(string? value)
{
    EnsureSeparator();
    var length = Encoding.GetByteCount(value); // what size of buffer is needed?
    var span = Writer.GetSpan(length);
    Encoding.GetBytes(value, span); // format to bytes
    Writer.Advance(length);
    shouldAppendSeparator = true;
}

## Optimize: encoding (again)
Our previous implementation of encoding CSV content was still wasteful, because we still had to materialize results into a short-lived `string`. So, we could spare that allocation and write directly the output buffer.

```cs
static readonly char[] ToEscape = new [] { ',', '"', '\n', '\r' };

public void WriteValue(string? value)
{
    EnsureSeparator();
    if (!String.IsNullOrEmpty(value))
    {
        // decide whether we have to escape or not
        var firstToEscape = value.IndexOfAny(ToEscape);
        if (firstToEscape == -1)
        {
            // no, write as is
            var length = Encoding.GetByteCount(value);
            var span = Writer.GetSpan(length);
            Encoding.GetBytes(value, span);
            Writer.Advance(length);
        }
        else
        {
            // yes, so add quotes
            WriteQuote();

            // decide which case it is
            var shouldEscape = value[firstToEscape] == '"' || value.IndexOf('"', firstToEscape + 1) != -1;
            if (shouldEscape)
            {
                // quotes must be escaped
                // TODO: write in segments
            }
            else
            {
                // write value
                var length = Encoding.GetByteCount(value);
                var span = Writer.GetSpan(length);
                Encoding.GetBytes(value, span);
                Writer.Advance(length);
            }

            // add quotes
            WriteQuote();
        }
    }
    shouldAppendSeparator = true;
}

private void WriteQuote()
{
    var span = Writer.GetSpan(1);
    span[0] = (byte)'"';
    Writer.Advance(1);
}
```

## Optimize: enumeration
Instead of using [IEnumerable&quot;T&quot;](https://docs.microsoft.com/en-us/dotnet/api/system.collections.generic.ienumerable-1), we can take advantage of [IAsyncEnumerable&quot;T&quot;](https://docs.microsoft.com/en-us/dotnet/api/system.collections.generic.iasyncenumerable-1), so the data source doesn't have to be buffer into memory all at once, but can be streamed:

```cs
public async Task WriteAsCsv<T>(IAsyncEnumerable<T> items, CsvWriter writer)
{
	var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
    await foreach (var item in items)
    {
        foreach (var property in properties)
        {
            var value = property.GetValue(entry);
            switch (value)
            {
                case int @int: writer.WriteValue(@int); break;
                case Guid guid: writer.WriteValue(guid); break;
                default: writer.WriteValue(value?.ToString());break;
            }
        }
    }
    await writer.FlushAsync();
}
```

## Optimize: reflection (caching)
TODO

## Optimize: reflection (emit)
TODO

## Optimize: reflection (source generators)
TODO
