using Avalonia.Platform;
using Avalonia.Threading;
using MonoMac.WebKit;

namespace AvaloniaScrollViewerExample.NativeControls;

public class EmbedSampleMac : INativeDemoControl
{
    private const string WEB_VIEW_HTML = @"
document.body.style.overflow = 'hidden';
document.body.style.margin = '0';

// Create a div element
var div = document.createElement('div');
div.style.width = '500px';
div.style.height = '500px';
div.style.backgroundColor = 'black';

// Append the div to the body of the document
document.body.appendChild(div);";

    public IPlatformHandle CreateControl()
    {
        // Note: We are using MonoMac for example purposes
        // It shouldn't be used in production apps
        MacHelper.EnsureInitialized();

        var webView = new WebView();
        Dispatcher.UIThread.Post(() => webView.StringByEvaluatingJavaScriptFromString(WEB_VIEW_HTML));

        return new MacOSViewHandle(webView);
    }
}
