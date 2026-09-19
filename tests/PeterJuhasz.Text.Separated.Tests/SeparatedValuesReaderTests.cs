namespace System.Text.Separated.Tests;

[TestClass]
public class SeparatedValuesReaderTests
{
	[TestMethod]
	public async Task Read()
	{
		var csv = """
			A,B,C
			1,2,3
			4,5,6
			7,8,9
			""";
		var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
		var reader = new SeparatedValuesReader<TestRecord>(new(HasHeader: true, Delimiter: ','));
		var records = await reader.ReadAsync(stream, default).ToListAsync(TestContext.CancellationToken);
		Assert.HasCount(3, records);
		Assert.AreEqual(new TestRecord(1, 2, 3), records[0]);
		Assert.AreEqual(new TestRecord(4, 5, 6), records[1]);
		Assert.AreEqual(new TestRecord(7, 8, 9), records[2]);
	}

	[TestMethod]
	public async Task Read_DifferentOrder()
	{
		var csv = """
			B,A,C
			2,1,3
			5,4,6
			8,7,9
			""";
		var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
		var reader = new SeparatedValuesReader<TestRecord>(new(HasHeader: true, Delimiter: ','));
		var records = await reader.ReadAsync(stream, default).ToListAsync(TestContext.CancellationToken);
		Assert.HasCount(3, records);
		Assert.AreEqual(new TestRecord(1, 2, 3), records[0]);
		Assert.AreEqual(new TestRecord(4, 5, 6), records[1]);
		Assert.AreEqual(new TestRecord(7, 8, 9), records[2]);
	}

	[TestMethod]
	public async Task Read_IgnoreOtherColumns()
	{
		var csv = """
			B,D,A,C
			2,6,1,3
			5,0,4,6
			8,1,7,9
			""";
		var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
		var reader = new SeparatedValuesReader<TestRecord>(new(
			HasHeader: true,
			Delimiter: ',',
			UnknownColumns: SeparatedValuesReaderOptions.UnknownColumnHandling.Ignore
		));
		var records = await reader.ReadAsync(stream, default).ToListAsync(TestContext.CancellationToken);
		Assert.HasCount(3, records);
		Assert.AreEqual(new TestRecord(1, 2, 3), records[0]);
		Assert.AreEqual(new TestRecord(4, 5, 6), records[1]);
		Assert.AreEqual(new TestRecord(7, 8, 9), records[2]);
	}

	[TestMethod]
	public async Task Read_Quoted()
	{
		var csv = """
			A,B,C,S
			1,2,3,a
			4,5,6,"b"
			7,8,9,"c,d"
			""";
		var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
		var reader = new SeparatedValuesReader<TestRecord>(new(HasHeader: true, Delimiter: ','));
		var records = await reader.ReadAsync(stream, default).ToListAsync(TestContext.CancellationToken);
		Assert.HasCount(3, records);
		Assert.AreEqual(new TestRecord(1, 2, 3, "a"), records[0]);
		Assert.AreEqual(new TestRecord(4, 5, 6, "b"), records[1]);
		Assert.AreEqual(new TestRecord(7, 8, 9, "c,d"), records[2]);
	}

	[TestMethod]
	public async Task Read_Quoted_EscapedQuote()
	{
		var csv = """
			A,B,C,S
			1,2,3,a
			4,5,6,"b"
			7,8,9,"c"",d"
			""";
		var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
		var reader = new SeparatedValuesReader<TestRecord>(new(HasHeader: true, Delimiter: ','));
		var records = await reader.ReadAsync(stream, default).ToListAsync(TestContext.CancellationToken);
		Assert.HasCount(3, records);
		Assert.AreEqual(new TestRecord(1, 2, 3, "a"), records[0]);
		Assert.AreEqual(new TestRecord(4, 5, 6, "b"), records[1]);
		Assert.AreEqual(new TestRecord(7, 8, 9, "c\",d"), records[2]);
	}

	[TestMethod]
	public async Task Read_EmptyStringAtLineEnd()
	{
		var csv = """
			A,B,C,S
			1,2,3,
			4,5,6,a
			7,8,9,"c"",d"
			""";
		var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
		var reader = new SeparatedValuesReader<TestRecord>(new(HasHeader: true, Delimiter: ','));
		var records = await reader.ReadAsync(stream, default).ToListAsync(TestContext.CancellationToken);
		Assert.HasCount(3, records);
		Assert.AreEqual(new TestRecord(1, 2, 3, ""), records[0]);
		Assert.AreEqual(new TestRecord(4, 5, 6, "a"), records[1]);
		Assert.AreEqual(new TestRecord(7, 8, 9, "c\",d"), records[2]);
	}


	[TestMethod]
	public async Task Read_ExtraValues()
	{
		var csv = """
			B,D,A,C
			2,6,1,3
			5,0,4,6
			8,1,7,9
			""";
		var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
		var reader = new SeparatedValuesReader<TestRecordExtra>(new(
			HasHeader: true,
			Delimiter: ',',
			UnknownColumns: SeparatedValuesReaderOptions.UnknownColumnHandling.LoadIntoExtraValues
		));
		var records = await reader.ReadAsync(stream, default).ToListAsync(TestContext.CancellationToken);
		Assert.HasCount(3, records);
		Assert.IsNotNull(records[0].ExtraValues);
		Assert.HasCount(1, records[0].ExtraValues!);
		Assert.AreEqual(2, records[0].B);
		Assert.AreEqual("6", records[0].ExtraValues!["D"]);
		Assert.AreEqual(1, records[0].A);
		Assert.AreEqual(3, records[0].C);

	}

	[TestMethod]
	public async Task Read_WithoutTrailingNewline()
	{
		// This test simulates a CSV file without an empty line at the end
		var csv = "A,B,C\n1,2,3\n4,5,6\n7,8,9";
		var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
		var reader = new SeparatedValuesReader<TestRecord>(new(HasHeader: true, Delimiter: ','));
		var records = await reader.ReadAsync(stream, default).ToListAsync(TestContext.CancellationToken);
		Assert.HasCount(3, records);
		Assert.AreEqual(new TestRecord(1, 2, 3), records[0]);
		Assert.AreEqual(new TestRecord(4, 5, 6), records[1]);
		Assert.AreEqual(new TestRecord(7, 8, 9), records[2]);
	}

	[TestMethod]
	public async Task Read_OnlyHeaderNoTrailingNewline()
	{
		// This test simulates a CSV file with only header and no trailing newline
		var csv = "A,B,C";
		var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
		var reader = new SeparatedValuesReader<TestRecord>(new(HasHeader: true, Delimiter: ','));
		var records = await reader.ReadAsync(stream, default).ToListAsync(TestContext.CancellationToken);
		Assert.IsEmpty(records);
	}

	[TestMethod]
	public async Task Read_OnlyHeaderWithTrailingNewline()
	{
		// This test simulates a CSV file with only header and a trailing newline
		var csv = "A,B,C\n";
		var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
		var reader = new SeparatedValuesReader<TestRecord>(new(HasHeader: true, Delimiter: ','));
		var records = await reader.ReadAsync(stream, default).ToListAsync(TestContext.CancellationToken);
		Assert.IsEmpty(records);
	}

	[TestMethod]
	public async Task Read_MissingColumnsInLastRowWithoutTrailingNewline()
	{
		// This test simulates a CSV where the last row has fewer columns and no trailing newline
		var csv = "A,B,C\n1,2,3\n4,5";
		var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
		var reader = new SeparatedValuesReader<TestRecord>(new(HasHeader: true, Delimiter: ','));
		var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
		{
			await reader.ReadAsync(stream, default).ToListAsync(TestContext.CancellationToken);
		});
		Assert.IsNotNull(exception);
	}

	[TestMethod]
	public async Task Read_WithTrailingEmptyLine()
	{
		// This test simulates a CSV with an empty line at the end (header + data + empty line)
		var csv = "A,B,C\n1,2,3\n4,5,6\n7,8,9\n\n";
		var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
		var reader = new SeparatedValuesReader<TestRecord>(new(HasHeader: true, Delimiter: ','));
		var records = await reader.ReadAsync(stream, default).ToListAsync(TestContext.CancellationToken);
		Assert.HasCount(3, records);
		Assert.AreEqual(new TestRecord(1, 2, 3), records[0]);
		Assert.AreEqual(new TestRecord(4, 5, 6), records[1]);
		Assert.AreEqual(new TestRecord(7, 8, 9), records[2]);
	}


	record class TestRecord(
		int A,
		int B,
		int C,
		string? S = null
	);

	record class TestRecordExtra(
		int A,
		int B,
		int C,
		IReadOnlyDictionary<string, string?>? ExtraValues = null
	);

	public TestContext TestContext { get; set; }
}