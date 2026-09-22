using System.IO.Pipelines;
using System.Text;

namespace PeterJuhasz.Text.Separated.Tests;

[TestClass]
public class SeparatedValuesWriterTests
{
	[TestMethod]
	public async Task WriteValue_RegularCase()
	{
		// Arrange
		using var stream = new MemoryStream();
		var pipe = PipeWriter.Create(stream);
		var options = SeparatedValuesWriterOptions.Csv;
		var writer = new SeparatedValuesWriter(pipe, options);

		// Act
		writer.WriteValue("hello");
		writer.WriteValue("world");
		writer.WriteLine();
		await writer.FlushAsync(default);

		// Assert
		var result = Encoding.UTF8.GetString(stream.ToArray());
		Assert.AreEqual("hello,world\n", result);
	}

	[TestMethod]
	public async Task WriteValue_QuoteInValue()
	{
		// Arrange
		using var stream = new MemoryStream();
		var pipe = PipeWriter.Create(stream);
		var options = SeparatedValuesWriterOptions.Csv;
		var writer = new SeparatedValuesWriter(pipe, options);

		// Act
		writer.WriteValue("say \"hello\"");
		writer.WriteValue("quote\"test");
		writer.WriteLine();
		await writer.FlushAsync(default);

		// Assert
		var result = Encoding.UTF8.GetString(stream.ToArray());
		Assert.AreEqual("\"say \"\"hello\"\"\",\"quote\"\"test\"\n", result);
	}

	[TestMethod]
	public async Task WriteValue_NewLineInValue()
	{
		// Arrange
		using var stream = new MemoryStream();
		var pipe = PipeWriter.Create(stream);
		var options = SeparatedValuesWriterOptions.Csv;
		var writer = new SeparatedValuesWriter(pipe, options);

		// Act
		writer.WriteValue("line1\nline2");
		writer.WriteValue("line1\nline2");
		writer.WriteLine();
		await writer.FlushAsync(default);

		// Assert
		var result = Encoding.UTF8.GetString(stream.ToArray());
		Assert.AreEqual("\"line1\nline2\",\"line1\nline2\"\n", result);
	}

	[TestMethod]
	public async Task WriteValue_SpecialCharacters()
	{
		// Arrange
		using var stream = new MemoryStream();
		var pipe = PipeWriter.Create(stream);
		var options = SeparatedValuesWriterOptions.Csv;
		var writer = new SeparatedValuesWriter(pipe, options);

		// Act
		writer.WriteValue("value,with,commas");
		writer.WriteValue("value;with;semicolons");
		writer.WriteValue("value\twith\ttabs");
		writer.WriteValue("émojis 🚀 and ñ");
		writer.WriteLine();
		await writer.FlushAsync(default);

		// Assert
		var result = Encoding.UTF8.GetString(stream.ToArray());
		Assert.AreEqual("\"value,with,commas\",value;with;semicolons,value\twith\ttabs,émojis 🚀 and ñ\n", result);
	}

	[TestMethod]
	public async Task WriteValue_CustomDelimiterAndQuote()
	{
		// Arrange
		using var stream = new MemoryStream();
		var pipe = PipeWriter.Create(stream);
		var options = new SeparatedValuesWriterOptions(Delimiter: ';', Quote: '\'');
		var writer = new SeparatedValuesWriter(pipe, options);

		// Act
		writer.WriteValue("value");
		writer.WriteValue("value'with'quotes");
		writer.WriteLine();
		await writer.FlushAsync(default);

		// Assert
		var result = Encoding.UTF8.GetString(stream.ToArray());
		Assert.AreEqual("value;'value''with''quotes'\n", result);
	}

	[TestMethod]
	public async Task WriteValue_NullValue()
	{
		// Arrange
		using var stream = new MemoryStream();
		var pipe = PipeWriter.Create(stream);
		var options = SeparatedValuesWriterOptions.Csv;
		var writer = new SeparatedValuesWriter(pipe, options);

		// Act
		writer.WriteValue("first");
		writer.WriteValue((string?)null);
		writer.WriteValue("third");
		writer.WriteLine();
		await writer.FlushAsync(default);

		// Assert
		var result = Encoding.UTF8.GetString(stream.ToArray());
		Assert.AreEqual("first,,third\n", result);
	}

	[TestMethod]
	public async Task WriteValue_EmptyValue()
	{
		// Arrange
		using var stream = new MemoryStream();
		var pipe = PipeWriter.Create(stream);
		var options = SeparatedValuesWriterOptions.Csv;
		var writer = new SeparatedValuesWriter(pipe, options);

		// Act
		writer.WriteValue("first");
		writer.WriteValue("");
		writer.WriteValue("third");
		writer.WriteLine();
		await writer.FlushAsync(default);

		// Assert
		var result = Encoding.UTF8.GetString(stream.ToArray());
		Assert.AreEqual("first,,third\n", result);
	}

	[TestMethod]
	public async Task WriteHeader_WithCustomOptions()
	{
		// Arrange
		using var stream = new MemoryStream();
		var pipe = PipeWriter.Create(stream);
		var options = new SeparatedValuesWriterOptions(HasHeader: true);
		var writer = new SeparatedValuesWriter(pipe, options);

		// Act
		writer.WriteHeader("Name");
		writer.WriteHeader("Value");
		writer.WriteLine();
		writer.WriteValue("Test");
		writer.WriteValue("123");
		writer.WriteLine();
		await writer.FlushAsync(default);

		// Assert
		var result = Encoding.UTF8.GetString(stream.ToArray());
		Assert.AreEqual("Name,Value\nTest,123\n", result);
	}

	[TestMethod]
	public async Task WriteLine_CustomNewLine()
	{
		// Arrange
		using var stream = new MemoryStream();
		var pipe = PipeWriter.Create(stream);
		var options = new SeparatedValuesWriterOptions(NewLine: "\n");
		var writer = new SeparatedValuesWriter(pipe, options);

		// Act
		writer.WriteValue("first");
		writer.WriteValue("second");
		writer.WriteLine();
		await writer.FlushAsync(default);

		// Assert
		var result = Encoding.UTF8.GetString(stream.ToArray());
		Assert.AreEqual("first,second\n", result);
	}
}