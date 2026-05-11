using System.Collections.Generic;
using System.Linq;

namespace PowerDesk.Core.Navigation;

public sealed class ModuleRegistry
{
    private readonly List<IPowerDeskModule> _modules = new();

    public IReadOnlyList<IPowerDeskModule> Modules => _modules;

    public void Register(IPowerDeskModule module)
    {
        if (_modules.Any(m => m.Id == module.Id)) return;
        _modules.Add(module);
    }

    public IPowerDeskModule? FindById(string id) => _modules.FirstOrDefault(m => m.Id == id);
}
