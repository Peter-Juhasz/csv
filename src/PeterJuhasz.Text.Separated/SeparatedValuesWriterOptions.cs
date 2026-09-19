namespace System.Text.Separated;

public readonly record struct SeparatedValuesWriterOptions(
	bool HasHeader = true,
	char Delimiter = ',',
	char Quote = '"',
	string NewLine = "\n"
)
{
	public static readonly SeparatedValuesWriterOptions Csv = new(Delimiter: ',');

	public static readonly SeparatedValuesWriterOptions Tsv = new(Delimiter: '\t');
}
