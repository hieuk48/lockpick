using CommunityToolkit.Mvvm.ComponentModel;

namespace Lockpick.App.ViewModels;

/// <summary>A part of a page, listed under it in the navigation (e.g. Settings → Overlay).</summary>
public sealed record PageSection(string Id, string Title)
{
    public override string ToString() => Title;
}

/// <summary>A top-level navigation page, optionally split into sections (only one shows at a time).</summary>
public abstract partial class PageViewModel(string title, string iconKey) : ObservableObject
{
    public string Title { get; } = title;

    /// <summary>Resource key of the page's Lucide icon geometry (see Assets/Icons.axaml).</summary>
    public string IconKey { get; } = iconKey;

    /// <summary>The page's sections, in the navigation's order; empty when the page is all one.</summary>
    public IReadOnlyList<PageSection> Sections { get; private set; } = [];

    public bool HasSections => Sections.Count > 0;

    /// <summary>The section shown (the first one at the start; each page remembers its own).</summary>
    [ObservableProperty]
    public partial PageSection? SelectedSection { get; set; }

    /// <summary>Shows the section <paramref name="id"/> (e.g. a link from another page).</summary>
    public void ShowSection(string id)
    {
        if (Sections.FirstOrDefault(s => s.Id == id) is { } section)
        {
            SelectedSection = section;
        }
    }

    /// <summary>Called when the page becomes visible or hidden, so pages can pause periodic work.</summary>
    public virtual void OnVisibilityChanged(bool visible)
    {
    }

    protected void SetSections(params PageSection[] sections)
    {
        Sections = sections;
        SelectedSection = sections.Length > 0 ? sections[0] : null;
        OnPropertyChanged(nameof(Sections));
        OnPropertyChanged(nameof(HasSections));
    }

    partial void OnSelectedSectionChanged(PageSection? value)
    {
        if (value is null && Sections.Count > 0)
        {
            SelectedSection = Sections[0]; // a list never leaves a page without a section
        }
    }
}
