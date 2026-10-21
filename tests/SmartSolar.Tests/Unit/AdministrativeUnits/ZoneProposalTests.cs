using System.Text;
using SmartSolar.Modules.AdministrativeUnits;
using SmartSolar.Modules.AdministrativeUnits.Import;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.AdministrativeUnits;

/// <summary>
/// guides/reference/operational-zones-initial-proposal.csv: a DATA ARTIFACT (applied nowhere) derived from the official legacy map —
/// one proposed zone per former district (zone_code KHU-&lt;ASCII slug&gt;, zone_name "Khu &lt;former district name without
/// Quận/Huyện/Thành phố/Thị xã&gt;"); PROPOSED for communes whose predecessors all come from one former district, NEEDS_REVIEW (with
/// every candidate former district) otherwise. The committed file must equal what this test generates; set
/// SMARTSOLAR_REGENERATE_ZONE_PROPOSAL=1 to rewrite it.
/// </summary>
public sealed class ZoneProposalTests
{
    public const string Header = "zone_code,zone_name,commune_code,commune_name,unit_type,status,former_districts";

    private static string ProposalPath()
    {
        var dataset = new DirectoryInfo(AdministrativeCatalogTestData.DatasetDirectory);
        var root = dataset.Parent!.Parent!.Parent!.Parent!.Parent!.Parent!;   // …/src/SmartSolar.Infrastructure/Persistence/ReferenceData/AdministrativeCatalog/<key>
        return Path.Combine(root.FullName, "guides", "reference", "operational-zones-initial-proposal.csv");
    }

    private static readonly string[] Prefixes = ["Thành phố ", "Thị xã ", "Quận ", "Huyện "];

    public static string ShortName(string formerDistrict) => Prefixes.FirstOrDefault(formerDistrict.StartsWith) is { } p ? formerDistrict[p.Length..] : formerDistrict;

    public static string Slug(string value)
    {
        var builder = new StringBuilder();
        foreach (var c in VietnameseText.Normalize(value).ToUpperInvariant())
            builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        return string.Join('-', builder.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Csv(string value) => value.Contains(',') || value.Contains('"') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    public static string Generate()
    {
        var rows = new List<string> { Header };
        var legacy = AdministrativeCatalogTestData.Legacy;
        foreach (var commune in AdministrativeCatalogTestData.Communes.OrderBy(c => c.Code, StringComparer.Ordinal))
        {
            var districts = legacy.Where(l => l.CommuneCode == commune.Code)
                .Select(l => (l.LegacyProvinceCode, l.LegacyDistrictCode, l.LegacyDistrictName)).Distinct()
                .OrderBy(d => d.LegacyProvinceCode, StringComparer.Ordinal).ThenBy(d => d.LegacyDistrictCode, StringComparer.Ordinal).ToList();
            var former = string.Join(" | ", districts.Select(d => $"{d.LegacyProvinceCode}:{d.LegacyDistrictCode} {d.LegacyDistrictName}"));
            var (code, name, status) = districts.Count == 1
                ? ($"KHU-{Slug(ShortName(districts[0].LegacyDistrictName))}", $"Khu {ShortName(districts[0].LegacyDistrictName)}", "PROPOSED")
                : ("", "", "NEEDS_REVIEW");
            rows.Add(string.Join(',', new[] { code, name, commune.Code, commune.Name, commune.UnitType, status, former }.Select(Csv)));
        }
        return string.Join('\n', rows) + "\n";
    }

    [Fact]
    public void The_committed_proposal_matches_the_official_legacy_map()
    {
        var expected = Generate();
        var path = ProposalPath();
        if (Environment.GetEnvironmentVariable("SMARTSOLAR_REGENERATE_ZONE_PROPOSAL") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, expected, new UTF8Encoding(false));
        }
        Assert.True(File.Exists(path), $"{path} is missing.");
        Assert.Equal(expected, File.ReadAllText(path, Encoding.UTF8));
    }

    [Fact]
    public void Every_code_of_the_proposal_is_an_official_commune_and_the_counts_hold()
    {
        var rows = CsvReader.Parse(File.ReadAllBytes(ProposalPath()));
        Assert.Equal(Header.Split(','), rows[0]);
        var body = rows.Skip(1).ToList();
        var official = AdministrativeCatalogTestData.Communes.ToDictionary(c => c.Code);
        Assert.Equal(168, body.Count);
        Assert.All(body, r => Assert.True(official.ContainsKey(r[2]) && official[r[2]].Name == r[3], $"unknown commune {r[2]} {r[3]}"));
        Assert.Equal(body.Count, body.Select(r => r[2]).Distinct().Count());
        Assert.Equal(151, body.Count(r => r[5] == "PROPOSED"));
        Assert.Equal(17, body.Count(r => r[5] == "NEEDS_REVIEW"));
        Assert.All(body.Where(r => r[5] == "NEEDS_REVIEW"), r => Assert.Contains(" | ", r[6]));
        Assert.All(body.Where(r => r[5] == "PROPOSED"), r => Assert.Matches("^KHU-[A-Z0-9][A-Z0-9-]*$", r[0]));
        // One zone code per former district (no slug collision between districts).
        var zones = body.Where(r => r[5] == "PROPOSED").GroupBy(r => r[0]).ToList();
        Assert.All(zones, g => Assert.Single(g.Select(r => r[6]).Distinct()));
        Assert.Contains(body, r => r[2] == "26824" && r[0] == "KHU-THU-DUC" && r[1] == "Khu Thủ Đức");
        Assert.Contains(body, r => r[2] == "26732" && r[0] == "KHU-CON-DAO" && r[4] == CommuneUnitTypes.SpecialZone);
    }
}
