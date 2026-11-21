using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cultivate;

/// <summary>
/// The first run's questions, one after another, so a new user is led through the values Cultivate needs. Enter accepts the value shown
/// or typed, Y and N answer a yes/no question, and Esc returns to the previous question, or leaves from the first one. The answers are
/// kept in a copy of the settings until the last question is answered.
/// </summary>
internal sealed class FirstRunScreen
{
    private readonly DeploymentState? active;
    private readonly bool firstRun;
    private readonly string path;
    private readonly IReadOnlyList<SettingField> fields = SettingField.All;
    internal Settings Draft { get; }
    internal int Step { get; private set; }
    internal SettingField Question => fields[Step];
    /// <summary>The field a text question is answered in; null for a yes/no question.</summary>
    internal TextField? Field { get; private set; }
    internal string? Error { get; private set; }
    /// <summary>A warning about the last answer, such as a webfarm that cannot be reached, shown above the next question.</summary>
    internal string? Notice { get; private set; }
    internal bool Completed { get; private set; }

    /// <summary>Asks for <paramref name="settings"/>' values without changing them; <paramref name="path"/> is shown as where they will be saved.</summary>
    internal FirstRunScreen(Settings settings, DeploymentState? active, bool firstRun, string path)
    {
        Draft = SettingField.Copy(settings);
        this.active = active;
        this.firstRun = firstRun;
        this.path = path;
        Open();
    }

    private void Open() => (Field, Error) = (Question.YesNo ? null : new TextField(Question.Read(Draft)), null);

    /// <summary>Handles a key; true when the last question is answered or Esc leaves the first.</summary>
    internal bool HandleKey(ConsoleKeyInfo key)
    {
        var question = Question;
        if (key.Key == ConsoleKey.Escape)
        {
            var previous = Step - 1;
            while (previous >= 0 && !fields[previous].Applies(Draft)) previous--;
            if (previous < 0) return true;
            (Step, Notice) = (previous, null);
            Open();
            return false;
        }
        if (question.YesNo)
        {
            bool? answer = key.Key switch { ConsoleKey.Y => true, ConsoleKey.N => false, ConsoleKey.Enter => question.Get!(Draft), _ => null };
            if (answer is not { } value) return false;
            question.Set!(Draft, value);
            return Next(null);
        }
        if (key.Key != ConsoleKey.Enter)
        {
            if (Field!.Handle(key)) Error = null;
            return false;
        }
        var (changed, error) = question.Change(Draft, Field!.Text, active);
        if (changed is null)
        {
            Error = error;
            return false;
        }
        SettingField.Apply(changed, Draft);
        return Next(question.Note?.Invoke(Draft) is { } note ? $"{question.Name}: {note}" : null);
    }

    private bool Next(string? notice)
    {
        Notice = notice;
        var next = Step + 1;
        while (next < fields.Count && !fields[next].Applies(Draft)) next++;
        if (next == fields.Count) return Completed = true;
        Step = next;
        Open();
        return false;
    }

    internal IRenderable Render(int width, int height)
    {
        var question = Question;
        IRenderable Line(string text, string color) => new Markup($"[{color}]{Markup.Escape(text)}[/]");
        var below = new List<IRenderable>
        {
            Line("Saved to " + SettingField.Fit(path, Math.Max(4, width - 10)), TerminalStyle.Muted),
            new Text("")
        };
        if (Notice is not null) below.AddRange(SettingField.Wrap(Notice, width - 1).Take(2).Select(line => Line(line, TerminalStyle.Gold)));
        below.Add(new Markup($"[{TerminalStyle.Muted}]Step {Step + 1} of {fields.Count}[/]   [bold {TerminalStyle.Accent}]{Markup.Escape(question.Name)}[/]" +
            (question.Optional ? $"   [{TerminalStyle.Muted}]optional[/]" : "")));
        below.AddRange(SettingField.Wrap(question.Description, width - 1).Take(3).Select(line => Line(line, TerminalStyle.Text)));
        below.Add(new Text(""));
        if (Field is not null) below.Add(new Markup($"[{TerminalStyle.Gold}]>[/] " + Field.Render(width - 3)));
        else
        {
            var yes = question.Get!(Draft);
            string Choice(string label, bool chosen) => chosen
                ? $"[bold {TerminalStyle.Accent} on {TerminalStyle.Surface}] {label} [/]" : $"[{TerminalStyle.Muted}] {label} [/]";
            below.Add(new Markup($"[{TerminalStyle.Gold}]>[/] {Choice("Yes", yes)} {Choice("No", !yes)}"));
        }
        if (Error is not null) below.AddRange(SettingField.Wrap(Error, width - 1).Take(2).Select(line => Line(line, "red")));
        below.Add(new Text(""));
        string Key(string key, string action) => $"[{TerminalStyle.Gold}]{key}[/] {action}   ";
        var back = Step == 0 ? "leave setup" : "previous question";
        below.Add(new Markup((Field is not null ? Key("Enter", "accept") + Key("Ctrl+U", "clear")
            : Key("Y", "yes") + Key("N", "no") + Key("Enter", "keep " + (question.Get!(Draft) ? "Yes" : "No"))) + $"[{TerminalStyle.Gold}]Esc[/] {back}"));
        // Every line below is a single line; one more allows for the tagline wrapping beside the seedling.
        var banner = new Banner(firstRun ? "WELCOME" : "SETTINGS",
            firstRun ? "Let's connect Cultivate to your MyMeyer checkout and dev webfarm." : "Some settings need fixing before Cultivate can start.",
            below.Count + 1);
        return new Rows([banner, .. below]);
    }
}
