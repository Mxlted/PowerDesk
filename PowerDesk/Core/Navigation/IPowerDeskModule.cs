using System.Threading.Tasks;
using UserControl = System.Windows.Controls.UserControl;

namespace PowerDesk.Core.Navigation;

/// <summary>
/// Contract every PowerDesk feature module implements so the shell can register and host it generically.
/// </summary>
public interface IPowerDeskModule
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    /// <summary>Symbolic key for the module's icon (resolved by the shell into a glyph or graphic).</summary>
    string IconKey { get; }
    /// <summary>Path-Data geometry for the module's nav/card icon. Returned directly to avoid
    /// shell-side switch tables that need editing whenever a new module ships.</summary>
    string IconGeometry { get; }
    bool RequiresAdminForFullControl { get; }

    /// <summary>The page-level view to host in the shell. Created once and reused.</summary>
    UserControl MainView { get; }

    Task InitializeAsync();
    Task ShutdownAsync();
}
