using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PeakRecorder;

/// <summary>
/// Turn 3a: full-window pre-match briefing, shown when a match is found
/// (opponent known, stage striking underway).
/// </summary>
internal sealed class BriefingWindow : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    public BriefingWindow(string opponent)
    {
        Text = "PeakRecorder — Match Found";
        ClientSize = new Size(900, 640);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(0x10, 0x14, 0x26);
        Controls.Add(_web);

        _ = InitAsync(opponent);
    }

    private async Task InitAsync(string opponent)
    {
        try
        {
            var env = await BriefingRuntime.GetEnvironmentAsync();
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                if (e.TryGetWebMessageAsString() == "close") Close();
            };
            _web.CoreWebView2.NavigateToString(BriefingHtml.Full(opponent));
        }
        catch (Exception ex)
        {
            Log.Write($"BriefingWindow WebView2 init failed: {ex.Message}");
            Close();
        }
    }
}
