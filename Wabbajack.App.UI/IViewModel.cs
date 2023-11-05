using ReactiveUI;

namespace Wabbajack.App.UI;

public interface IViewModel : IActivatableViewModel
{
    public Type ViewModelInterface { get; }
}
