using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Windows;

namespace RemotePlay
{
    [ExcludeFromCodeCoverage]
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();
            SetVersion();
        }

        private void SetVersion()
        {
            var ver = Assembly.GetExecutingAssembly()
                               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                               ?.InformationalVersion
                      ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                      ?? string.Empty;

            // Trim any commit-hash suffix (e.g. "2.1.3+abc123" → "2.1.3")
            var plus = ver.IndexOf('+');
            if (plus >= 0) ver = ver[..plus];

            if (!string.IsNullOrEmpty(ver))
                VersionText.Text = "v" + ver;
        }

        /// <summary>
        /// Fades the splash out then closes it.
        /// Call this from the UI thread once the main window is ready.
        /// </summary>
        public void FadeOutAndClose()
        {
            var fade = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 1, To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(300)),
                FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
            };
            fade.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, fade);
        }
    }
}
