using DevFlow.Core;
using System.Xml;
using System.Xml.Linq;
namespace DevFlow.Infrastructure;
public static class TrxEvidence
{
    public static ValidationEvidence Read(string path, int exitCode)
    {
        if (!File.Exists(path)) return new(exitCode, 0, 0, [], []);
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var document = XDocument.Load(reader);
        var counters = document.Descendants().SingleOrDefault(e => e.Name.LocalName == "Counters");
        var results = document.Descendants().Where(e => e.Name.LocalName == "UnitTestResult").ToArray();
        string[] Ids(string outcome) => results.Where(e => (string?)e.Attribute("outcome") == outcome)
            .Select(e => (string?)e.Attribute("testId") ?? "").Where(id => id.Length > 0).Distinct().ToArray();
        return new(exitCode, (int?)counters?.Attribute("total") ?? 0, (int?)counters?.Attribute("failed") ?? 0,
            Ids("Passed"), Ids("Failed"));
    }
}
