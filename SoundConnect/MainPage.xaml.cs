namespace SoundConnect
{
    public partial class MainPage : ContentPage
    {
        public MainPage()
        {
            InitializeComponent();
        }

        /// <summary>界面第一次显示时挂上系统托盘图标（顺带接管"关窗口收进托盘"）。</summary>
        protected override void OnAppearing()
        {
            base.OnAppearing();
            WinUI.TrayIconHost.EnsureStarted(
                Handler?.MauiContext?.Services,
                Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()
            );
        }
    }
}
