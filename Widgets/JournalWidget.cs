using Microsoft.Windows.Widgets.Providers;
using WindowsJournal.Services;

namespace WindowsJournal.Widgets;

/// <summary>
/// Sends the widgets their content (see <see cref="WidgetContent"/>). It never shows entry text, so they're safe
/// even when Journal is locked.
/// </summary>
public static class JournalWidget
{
    /// <summary>Refreshes one widget with the latest progress and today's prompt.</summary>
    public static async Task UpdateAsync(WidgetContext widget)
    {
        try
        {
            var entries = (await JournalStore.ReadSnapshotAsync()).Where(e => !e.IsDraft).ToList();
            var data = WidgetContent.Data(
                SuggestionService.CurrentStreak(entries),
                entries.Select(e => e.CreatedAt.Date),
                DateTime.Today,
                System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek,
                SuggestionService.PromptOfTheDay());

            WidgetManager.GetDefault().UpdateWidget(new WidgetUpdateRequestOptions(widget.Id)
            {
                Template = WidgetContent.Template(widget.DefinitionId),
                Data = data,
            });
        }
        catch (Exception ex)
        {
            AIService.LogError(ex);
        }
    }

    /// <summary>Refreshes every Journal widget the user has pinned (called by the app after entries change).</summary>
    public static async Task UpdateAllAsync()
    {
        try
        {
            foreach (var info in JournalWidgetProvider.PinnedWidgets())
            {
                await UpdateAsync(info.WidgetContext);
            }
        }
        catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x80040154))
        {
            // "Class not registered": this PC has no Widgets board running, so there's nothing to refresh.
        }
        catch (Exception ex)
        {
            AIService.LogError(ex);
        }
    }
}

/// <summary>The COM object the Widgets board talks to. One per helper process.</summary>
public sealed partial class JournalWidgetProvider : IWidgetProvider
{
    public const string ClassId = "9C1E4B7A-3D2F-4E8B-A6C5-71F0D2B9E4A3";

    private static readonly HashSet<string> Pinned = [];

    /// <summary>Raised when the last widget is removed, so the helper process can exit.</summary>
    public static event EventHandler? LastWidgetRemoved;

    /// <summary>The pinned widgets. Windows returns null, not an empty list, when there are none.</summary>
    public static WidgetInfo[] PinnedWidgets() => WidgetManager.GetDefault().GetWidgetInfos() ?? [];

    public JournalWidgetProvider()
    {
        // Widgets pinned before this helper started still need refreshing.
        lock (Pinned)
        {
            foreach (var info in PinnedWidgets()) Pinned.Add(info.WidgetContext.Id);
        }
    }

    public void CreateWidget(WidgetContext widgetContext)
    {
        lock (Pinned) Pinned.Add(widgetContext.Id);
        _ = JournalWidget.UpdateAsync(widgetContext);
    }

    public void DeleteWidget(string widgetId, string customState)
    {
        bool empty;
        lock (Pinned)
        {
            Pinned.Remove(widgetId);
            empty = Pinned.Count == 0;
        }

        if (empty) LastWidgetRemoved?.Invoke(this, EventArgs.Empty);
    }

    // The Write button is an OpenUrl action handled by the Widgets board, so there's nothing to do here.
    public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs) { }

    public void OnWidgetContextChanged(WidgetContextChangedArgs contextChangedArgs) =>
        _ = JournalWidget.UpdateAsync(contextChangedArgs.WidgetContext);

    // Called when the Widgets board opens: refresh so the week and streak are right after midnight, too.
    public void Activate(WidgetContext widgetContext) => _ = JournalWidget.UpdateAsync(widgetContext);

    public void Deactivate(string widgetId) { }
}
