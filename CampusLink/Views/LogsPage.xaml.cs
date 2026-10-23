using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using CampusLink.Infrastructure;

namespace CampusLink.Views
{
    public partial class LogsPage : UserControl
    {
        private readonly LogService _log;

        public LogsPage(LogService log)
        {
            InitializeComponent();
            _log = log;
            DataContext = log;
            log.Entries.CollectionChanged += Entries_CollectionChanged;
        }

        private void Entries_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action != NotifyCollectionChangedAction.Add) return;
            var items = LogList.Items;
            if (items.Count > 0) LogList.ScrollIntoView(items[items.Count - 1]);
        }

        private void Clear_OnClick(object sender, RoutedEventArgs e)
        {
            _log.Clear();
        }

        private void OpenFolder_OnClick(object sender, RoutedEventArgs e)
        {
            Services.ShellHelper.OpenFolder(_log.LogDirectory, _log);
        }
    }
}
