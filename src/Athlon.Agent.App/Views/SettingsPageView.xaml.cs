using System.Windows.Controls;
using Athlon.Agent.App.Behaviors;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Views;

public partial class SettingsPageView : UserControl
{
    public SettingsPageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SettingsViewModel previous)
        {
            previous.SyncPendingSecrets = null;
        }

        if (e.NewValue is not SettingsViewModel settings)
        {
            return;
        }

        settings.SyncPendingSecrets = () =>
        {
            if (!settings.IsApiKeyRevealed)
            {
                PasswordBoxBindingBehavior.SyncBoundPassword(ApiKeyPasswordBox);
            }

            if (!settings.IsKnowledgeEmbeddingApiKeyRevealed)
            {
                PasswordBoxBindingBehavior.SyncBoundPassword(KnowledgeEmbeddingApiKeyPasswordBox);
            }
        };
    }
}
