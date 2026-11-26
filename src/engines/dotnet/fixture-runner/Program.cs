using FixtureRunner;

if (args.Length >= 2 && args[0] == "--print")
{
    var fx = Fixture.Parse(File.ReadAllText(args[1]));
    foreach (var kv in fx.Run().Final.OrderBy(k => fx.NameOf(k.Key), StringComparer.Ordinal))
        Console.WriteLine($"{fx.NameOf(kv.Key)} {Fixture.Fmt(kv.Value)}");
    return 0;
}
if (args.Length < 1) { Console.Error.WriteLine("usage: FixtureRunner [--print] <file-or-dir>"); return 2; }
var files = Directory.Exists(args[0])
    ? Directory.GetFiles(args[0], "*.layout").OrderBy(s => s, StringComparer.Ordinal).ToArray()
    : [args[0]];
int failed = 0;
foreach (var file in files)
{
    var err = Fixture.Parse(File.ReadAllText(file)).Check();
    if (err is null) Console.WriteLine($"PASS {file}");
    else { failed++; Console.WriteLine($"FAIL {file}: {err}"); }
}
return failed == 0 ? 0 : 1;
