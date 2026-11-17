using System;
using System.Collections;
using System.Collections.Generic;

public sealed partial class IntegrationPlugin
{
    private string RunProgress => Host.GetType().GetProperty("CompilationState", All)!.GetValue(Host)!.ToString()!;
    private static string OutputDocument => Localizer.LoadDoc("docs/output.md");
    private IEnumerator OutputStartup()
    {
        yield return Load("OutputStartup", new Dictionary<string, string> { ["main"] =
            "@main def run(): Unit = { quick_print(\"STARTED_OUTPUT\"); while (true) { move(East) } }\n" });
        Check(!OutputDocument.Contains("Truncated Output:") && !OutputDocument.Contains("Compiling..."), "idle Scala output does not claim compilation or execution has started");
        Main.PressExecuteOrStop();
        Check(OutputDocument.Contains("Compiling...") && !OutputDocument.Contains("Done") && !OutputDocument.Contains("Truncated Output:"), "Play immediately shows only pending compilation status below the full-output link");
        bool workerStartupSeen = false, pendingHeadingCorrect = true;
        yield return Wait(() => {
            if (RunProgress == "Done" || !Running) return true;
            if (!Compiling && RunProgress == "Compiling") workerStartupSeen = true;
            pendingHeadingCorrect &= !OutputDocument.Contains("Truncated Output:") && !OutputDocument.Contains("Compiling... Done");
            return false;
        }, "worker entering actual player execution");
        Check(workerStartupSeen && pendingHeadingCorrect, "finished builds keep Compiling pending and hide truncated output throughout worker initialization");
        Check(RunProgress == "Done" && OutputDocument.Contains("Compiling... Done") && OutputDocument.Contains("Truncated Output:"), "execution-start notification reveals Done and the native truncated-output heading together");
        yield return Wait(() => Output.Contains("STARTED_OUTPUT"), "actual user output after startup");
        Main.PressExecuteOrStop(); yield return Wait(() => !Running, "stop output test program");
        Check(OutputDocument.Contains("Compiling... Done"), "completed startup status remains visible after stopping execution");
        Main.PressExecuteOrStop();
        Check(RunProgress == "Compiling" && !OutputDocument.Contains("Truncated Output:"), "another Play resets the heading even when compilation can use a cached artifact");
        Main.PressExecuteOrStop();
        Check(RunProgress == "Cancelled" && !OutputDocument.Contains("Truncated Output:"), "cancelling startup never claims the program began executing");
        Edit(Main, "val invalid: Int = \"wrong\"\n");
        Main.PressExecuteOrStop(); yield return Wait(() => !Running, "failed compilation in output status test");
        Check(RunProgress == "Failed" && OutputDocument.Contains("Compiling... Failed") && !OutputDocument.Contains("Truncated Output:") && OutputDocument.Contains("{{output}}"), "compile failures retain live error output without a premature execution heading");
        Edit(Main, "val answer: Int = 42\n");
        Main.PressExecuteOrStop(); yield return Wait(() => !Running, "pure program without game RPC or stdout");
        Check(RunProgress == "Done" && OutputDocument.Contains("Truncated Output:"), "a program with no game calls or output still reports that execution began");
    }
}
