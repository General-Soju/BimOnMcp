using System;
using System.Collections.Generic;

// Event-contract tests only; these are not evidence for an Autodesk version.
namespace Dynamo.Applications
{
    public static class DynamoRevit { public static FakeModel? RevitDynamoModel { get; set; } }
    public sealed class FakeModel
    {
        public FakeWorkspace CurrentWorkspace { get; } = new();
        public bool ThrowAfterStart { get; set; }
        public sealed class RunCancelCommand { public RunCancelCommand(bool errors, bool cancel) { } }
        public void ExecuteCommand(RunCancelCommand command)
        {
            CurrentWorkspace.Start();
            if (ThrowAfterStart) throw new InvalidOperationException("injected after scheduling");
        }
    }
    public sealed class FakeWorkspace
    {
        public List<object> Nodes { get; } = new();
        public List<object> Connectors { get; } = new();
        public FakeSettings RunSettings { get; } = new();
        public event EventHandler? EvaluationStarted;
        public event EventHandler? EvaluationCompleted;
        public event EventHandler? RefreshCompleted;
        public void Start() => EvaluationStarted?.Invoke(this, EventArgs.Empty);
        public void Complete() => EvaluationCompleted?.Invoke(this, EventArgs.Empty);
        public void Refresh() => RefreshCompleted?.Invoke(this, EventArgs.Empty);
    }
    public sealed class FakeSettings { public string RunType { get; set; } = "Manual"; public bool RunEnabled => true; }
}
