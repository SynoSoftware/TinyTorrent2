using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Synapse_Tests;

public sealed partial class UnitTestAppWindow : Window
{
    public UnitTestAppWindow() => InitializeComponent();

    /// <summary>The live visual tree that tests attach a control to so that <c>Loaded</c> fires.</summary>
    public Grid RootPanel => Root;
}
