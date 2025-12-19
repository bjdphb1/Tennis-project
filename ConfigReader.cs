using IniParser;
using IniParser.Model;

class ConfigReader
{
    public int NumMatchesPerCycle { get; private set; }
    public double PercentWagerPerCycle { get; private set; }
    public int MatchTimeOutDuration { get; private set; }
    public string Currency { get; private set; }
    public int DefaultStakeAmount { get; private set; }

    public string ApiKey { get; set; }

    public ConfigReader(string path)
    {
        var parser = new FileIniDataParser();
        IniData data = parser.ReadFile(path);

        NumMatchesPerCycle = int.Parse(data["AppSettings"]["NumMatchesPerCycle"]);
        PercentWagerPerCycle = double.Parse(data["AppSettings"]["PercentWagerPerCycle"]);
        MatchTimeOutDuration = int.Parse(data["AppSettings"]["MatchTimeOutDuration"]);
        Currency = data["AppSettings"]["Currency"];
        DefaultStakeAmount = int.TryParse(data["AppSettings"]["DefaultStakeAmount"], out int stake) ? stake : 10;
        ApiKey = data["AppSettings"]["APIKey"];
    }
}