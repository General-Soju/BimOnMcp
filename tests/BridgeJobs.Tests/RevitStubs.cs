// Host-free contract tests only; these stubs are never compiled into production.
using System.Collections.Generic;
namespace Autodesk.Revit.DB
{
    public sealed class Document
    {
        public override int GetHashCode() => IsValidObject ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this) : throw new System.InvalidOperationException("Closed document native hash");
        public override bool Equals(object? other)
        {
            if (!IsValidObject || other is Document d && !d.IsValidObject) throw new System.InvalidOperationException("Closed document native equality");
            return object.ReferenceEquals(this, other);
        }
        public bool IsValidObject { get; set; } = true;
        public bool IsLinked { get; set; }
        public bool IsReadOnly { get; set; }
        public bool IsFamilyDocument { get; set; }
        public string Title { get; set; } = "test";
    }
}
namespace Autodesk.Revit.UI
{
    public enum ExternalEventRequest { Accepted, Pending, Denied, TimedOut }
    public sealed class ExternalEvent { public ExternalEventRequest Raise() => ExternalEventRequest.Accepted; }
    public interface IExternalEventHandler { void Execute(UIApplication app); string GetName(); }
    public sealed class UIApplication
    {
        public TestApplication Application { get; } = new TestApplication();
        public UIDocument? ActiveUIDocument { get; set; }
    }
    public sealed class UIDocument { public Autodesk.Revit.DB.Document Document { get; set; } = new Autodesk.Revit.DB.Document(); }
    public sealed class TestApplication
    {
        public List<Autodesk.Revit.DB.Document> Documents { get; } = new List<Autodesk.Revit.DB.Document>();
        public string VersionNumber => "test";
        public string VersionBuild => "test";
    }
}
namespace Autodesk.Revit.UI.Events { public class IdlingEventArgs : System.EventArgs { } }
