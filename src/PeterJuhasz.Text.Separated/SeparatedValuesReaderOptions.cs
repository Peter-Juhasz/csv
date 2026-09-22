namespace PeterJuhasz.Text.Separated;

public record class SeparatedValuesReaderOptions(
	bool HasHeader = true,
	char Delimiter = ',',
	char Quote = '"',
	SeparatedValuesReaderOptions.UnknownColumnHandling UnknownColumns = SeparatedValuesReaderOptions.UnknownColumnHandling.Ignore
)
{
	public static readonly SeparatedValuesReaderOptions Csv = new(Delimiter: ',');

	public static readonly SeparatedValuesReaderOptions Tsv = new(Delimiter: '\t');

	public enum UnknownColumnHandling
	{
		Ignore,
		Throw,
		LoadIntoExtraValues,
	}
}
