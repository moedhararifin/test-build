namespace Dollhouse.Core;

public sealed class CommandOptions
{
    private readonly Dictionary<string, string?> values = new(StringComparer.Ordinal);

    public CommandOptions(string[] args, params string[] switches)
    {
        var flags = switches.ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            if (!name.StartsWith("--", StringComparison.Ordinal) || values.ContainsKey(name))
                throw new ArgumentException($"Invalid or duplicate option: {name}");
            string? value = null;
            if (!flags.Contains(name))
            {
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Missing value for {name}");
                value = args[i];
            }
            values.Add(name, value);
        }
    }

    public string? Get(string name) => values.GetValueOrDefault(name);
    public bool Has(string name) => values.ContainsKey(name);
    public void Allow(params string[] names)
    {
        var unknown = values.Keys.Except(names, StringComparer.Ordinal).FirstOrDefault();
        if (unknown is not null)
            throw new ArgumentException($"Unknown option: {unknown}");
    }
}
