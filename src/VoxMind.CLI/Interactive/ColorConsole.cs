namespace VoxMind.CLI.Interactive;

public static class ColorConsole
{
    public static void WriteHeader(string title)
    {
        var border = new string('─', title.Length + 4);
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"┌{border}┐");
        Console.WriteLine($"│  {title}  │");
        Console.WriteLine($"└{border}┘");
        Console.ResetColor();
    }

    public static void WriteInfo(string message)
    {
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    public static void WriteSuccess(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[OK] {message}");
        Console.ResetColor();
    }

    public static void WriteError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"[ERREUR] {message}");
        Console.ResetColor();
    }

    public static void WriteWarning(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[AVERT.] {message}");
        Console.ResetColor();
    }

    public static void WritePrompt()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("VoxMind> ");
        Console.ResetColor();
    }

    public static void WriteSessionStatus(string name, string duration, string[] participants, int segments)
    {
        var maxLen = Math.Max(name.Length, 30);
        var border = new string('─', maxLen + 2);
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"┌─{border}─┐");
        Console.WriteLine($"│  Session: {name.PadRight(maxLen)}│");
        Console.WriteLine($"│  Durée:   {duration.PadRight(maxLen)}│");
        Console.WriteLine($"│  Partici: {string.Join(", ", participants).PadRight(maxLen)}│");
        Console.WriteLine($"│  Segments:{segments.ToString().PadRight(maxLen)}│");
        Console.WriteLine($"└─{border}─┘");
        Console.ResetColor();
    }
}
