﻿
using ActiproSoftware.Windows.Controls.DataGrid;
using ActiproSoftware.Windows.Controls.Docking;
using ActiproSoftware.Windows.Themes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static EQLogParser.DataManager;

namespace EQLogParser
{
  public partial class MainWindow : Window
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private const string APP_NAME = "EQLogParser";
    private const string VERSION = "v1.0.18";
    private const string VERIFIED_PETS = "Verified Pets";
    private const string DPS_LABEL = " No NPCs Selected";
    private const string SHARE_DPS_LABEL = "No Players Selected";
    private const string SHARE_DPS_TOO_BIG_LABEL = "Exceeded Copy/Paste Limit for EQ";
    private const int MIN_LINE_LENGTH = 33;
    private const int DISPATCHER_DELAY = 100; // millis

    private static SolidColorBrush NORMAL_BRUSH = new SolidColorBrush(Color.FromRgb(37, 37, 38));
    private static SolidColorBrush BREAK_TIME_BRUSH = new SolidColorBrush(Color.FromRgb(150, 65, 13));
    private static SolidColorBrush WARNING_BRUSH = new SolidColorBrush(Color.FromRgb(241, 109, 29));
    private static SolidColorBrush BRIGHT_TEXT_BRUSH = new SolidColorBrush(Colors.White);
    private static SolidColorBrush LIGHTER_BRUSH = new SolidColorBrush(Color.FromRgb(90, 90, 90));
    private static SolidColorBrush GOOD_BRUSH = new SolidColorBrush(Colors.LightGreen);
    private static BitmapImage COLLAPSE_BITMAP = new BitmapImage(new Uri(@"pack://application:,,,/icons/Collapse_16x.png"));
    private static BitmapImage EXPAND_BITMAP = new BitmapImage(new Uri(@"pack://application:,,,/icons/Expand_16x.png"));

    private static ActionProcessor CastLineProcessor;
    private static ActionProcessor DamageLineProcessor;
    private static ActionProcessor LinePreProcessor;

    private ObservableCollection<SortableName> VerifiedPetsView = new ObservableCollection<SortableName>();
    private ObservableCollection<SortableName> VerifiedPlayersView = new ObservableCollection<SortableName>();
    private ObservableCollection<NonPlayer> NonPlayersView = new ObservableCollection<NonPlayer>();
    private ObservableCollection<PetMapping> PetPlayersView = new ObservableCollection<PetMapping>();

    // stats
    private static bool NeedStatsUpdate = false;
    private static bool UpdatingStats = false;
    private static bool NeedDPSTextUpdate = false;
    private static CombinedStats CurrentStats = null;
    private DispatcherTimer NonPlayerSelectionTimer;
    private int CurrentFightID = 0;

    // progress window
    private static bool UpdatingProgress = false;
    private static long ProcessedBytes = 0; // EOF
    private static DateTime StartLoadTime; // millis
    private static bool MonitorOnly;

    // tab counts
    private static DocumentWindow spellCastsWindow = null;

    private static NpcDamageManager NpcDamageManager = null;
    private LogReader EQLogReader = null;
    private bool NeedScrollIntoView = false;

    public MainWindow()
    {
      try
      {
        InitializeComponent();
        LOG.Info("Initialized Components");

        // update titles
        Title = APP_NAME + " " + VERSION;
        dpsTitle.Content = DPS_LABEL;

        // pet -> players
        petMappingGrid.ItemsSource = PetPlayersView;
        DataManager.Instance.EventsNewPetMapping += (sender, mapping) => Dispatcher.InvokeAsync(() =>
        {
          PetPlayersView.Add(mapping);
          NeedStatsUpdate = true;
        });

        // verified pets table
        verifiedPetsGrid.ItemsSource = VerifiedPetsView;
        DataManager.Instance.EventsNewVerifiedPet += (sender, name) => Dispatcher.InvokeAsync(() =>
        {
          VerifiedPetsView.Add(new SortableName() { Name = name });
          verifiedPetsWindow.Title = "Verified Pets (" + VerifiedPetsView.Count + ")";
        });

        // verified player table
        verifiedPlayersGrid.ItemsSource = VerifiedPlayersView;
        DataManager.Instance.EventsNewVerifiedPlayer += (sender, name) => Dispatcher.InvokeAsync(() =>
        {
          VerifiedPlayersView.Add(new SortableName() { Name = name });
          verifiedPlayersWindow.Title = "Verified Players (" + VerifiedPlayersView.Count + ")";
        });

        // List of NPCs to select from, damage is saved in the NonPlayer object
        npcDataGrid.ItemsSource = NonPlayersView;
        DataManager.Instance.EventsUpdatedNonPlayer += (sender, npc) => NeedStatsUpdate = (CurrentStats != null && CurrentStats.NpcIDs.Contains(npc.ID));
        DataManager.Instance.EventsRemovedNonPlayer += (sender, name) => RemoveNonPlayer(name);
        DataManager.Instance.EventsNewNonPlayer += (sender, npc) => AddNonPlayer(npc);
        DataManager.Instance.EventsNewUnverifiedPetOrPlayer += (sender, name) => RemoveNonPlayer(name);

        // fix player DPS table sorting
        playerDataGrid.Sorting += (s, e) =>
        {
          if (e.Column.Header != null && (e.Column.Header.ToString() != "Name" && e.Column.Header.ToString() != "Additional Details"))
          {
            e.Column.SortDirection = e.Column.SortDirection ?? ListSortDirection.Ascending;
          }
        };

        DispatcherTimer dispatcherTimer = new DispatcherTimer();
        dispatcherTimer.Tick += new EventHandler(DispatcherTimer_Tick);
        dispatcherTimer.Interval = new TimeSpan(0, 0, 0, 0, DISPATCHER_DELAY);
        dispatcherTimer.Start();

        NonPlayerSelectionTimer = new DispatcherTimer();
        NonPlayerSelectionTimer.Tick += NonPlayerSelectionTimer_Tick;
        NonPlayerSelectionTimer.Interval = new TimeSpan(0, 0, 0, 0, 300);

        ThemeManager.BeginUpdate();
        try
        {
          // Use the Actipro styles for native WPF controls that look great with Actipro's control products
          ThemeManager.AreNativeThemesEnabled = true;

          SharedThemeCatalogRegistrar.Register();
          DockingThemeCatalogRegistrar.Register();

          // Default the theme to Metro Light
          ThemeManager.CurrentTheme = ThemeName.MetroDark.ToString();
        }
        finally
        {
          ThemeManager.EndUpdate();
        }
      }
      catch (Exception e)
      {
        LOG.Error(e);
      }
    }

    private void Window_Closed(object sender, System.EventArgs e)
    {
      StopProcessors();
      Application.Current.Shutdown();
    }

    private void DispatcherTimer_Tick(object sender, EventArgs e)
    {
      UpdateLoadingProgress();
      UpdateStats();
      UpdateDPSText();

      if (NeedScrollIntoView)
      {
        npcDataGrid.ScrollIntoView(npcDataGrid.Items.CurrentItem);
        NeedScrollIntoView = false;
      }
    }

    // Main Menu
    private void MenuItemWindow_Click(object sender, RoutedEventArgs e)
    {
      if (e.Source == npcWindowMenuitem)
      {
        Utils.OpenWindow(npcWindow);
      }
      else if (e.Source == fileProgressWindowMenuItem)
      {
        Utils.OpenWindow(progressWindow);
      }
      else if (e.Source == petMappingWindowMenuItem)
      {
        Utils.OpenWindow(petMappingWindow);
      }
      else if (e.Source == verifiedPlayersWindowMenuItem)
      {
        Utils.OpenWindow(verifiedPlayersWindow);
      }
      else if (e.Source == verifiedPetsWindowMenuItem)
      {
        Utils.OpenWindow(verifiedPetsWindow);
      }
      else if (e.Source == playerDPSTextWindowMenuItem)
      {
        Utils.OpenWindow(playerDPSTextWindow);
      }
    }

    // Main Menu Op File
    private void MenuItemSelectMonitorLogFile_Click(object sender, RoutedEventArgs e)
    {
      OpenLogFile(true);
    }

    private void MenuItemSelectLogFile_Click(object sender, RoutedEventArgs e)
    {
      MenuItem item = sender as MenuItem;
      int lastMins = -1;
      if (item != null && item.Tag != null && item.Tag.ToString() != "")
      {
        lastMins = Convert.ToInt32(item.Tag.ToString()) * 60;
      }

      OpenLogFile(false, lastMins);
    }

    // NonPlayer Window
    private void AddNonPlayer(NonPlayer npc)
    {
      Dispatcher.InvokeAsync(() =>
      {
        npc.FightID = CurrentFightID;
        NonPlayersView.Add(npc);
        npcDataGrid.Items.MoveCurrentToLast();
        if (!npcDataGrid.IsMouseOver)
        {
          NeedScrollIntoView = true;
        }
      });
    }

    private void RemoveNonPlayer(string name)
    {
      Dispatcher.InvokeAsync(() =>
      {
        int i = 0;
        foreach (NonPlayer item in NonPlayersView.Reverse())
        {
          i++;
          if (name == item.Name)
          {
            NonPlayersView.Remove(item);
            npcDataGrid.Items.Refresh(); // re-numbers
          }
        }
      });
    }

    private void NonPlayerSelectionTimer_Tick(object sender, EventArgs e)
    {
      NeedStatsUpdate = true;
      NonPlayerSelectionTimer.Stop();
    }

    private void NonPlayerDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      // adds a delay where a drag-select doesn't keep sending events
      NonPlayerSelectionTimer.Stop();
      NonPlayerSelectionTimer.Start();

      ThemedDataGrid callingDataGrid = sender as ThemedDataGrid;
      npcMenuItemSelectAll.IsEnabled = (callingDataGrid.SelectedItems.Count < callingDataGrid.Items.Count) && callingDataGrid.Items.Count > 0;
      npcMenuItemUnselectAll.IsEnabled = callingDataGrid.SelectedItems.Count > 0 && callingDataGrid.Items.Count > 0;
    }

    private void NonPlayerDataGridSelectFight_Click(object sender, RoutedEventArgs e)
    {
      ContextMenu menu = (sender as FrameworkElement).Parent as ContextMenu;
      ThemedDataGrid callingDataGrid = menu.PlacementTarget as ThemedDataGrid;
      NonPlayer npc = callingDataGrid.SelectedItem as NonPlayer;
      if (npc != null && npc.FightID > -1)
      {
        Parallel.ForEach(NonPlayersView, (one) =>
        {
          if (one.FightID == npc.FightID)
          {
            Dispatcher.InvokeAsync(() => callingDataGrid.SelectedItems.Add(one));
          }
        });
      }
    }

    private void NonPlayerDataGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
      DataGrid_LoadingRow(sender, e);

      NonPlayer npc = e.Row.Item as NonPlayer;
      if (npc != null && npc.BeginTimeString == NonPlayer.BREAK_TIME)
      {
        if (e.Row.Background != BREAK_TIME_BRUSH)
        {
          e.Row.Background = BREAK_TIME_BRUSH;
        }
      }
      else if (e.Row.Background != NORMAL_BRUSH)
      {
        e.Row.Background = NORMAL_BRUSH;
      }

      if (npcMenuItemSelectFight.IsEnabled == false)
      {
        npcMenuItemSelectFight.IsEnabled = true;
      }
    }

    private void DataGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
      e.Row.Header = (e.Row.GetIndex() + 1).ToString();
    }

    // Player DPS Data Grid
    private void PlayerDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      NeedDPSTextUpdate = true;

      ThemedDataGrid callingDataGrid = sender as ThemedDataGrid;
      pdgMenuItemSelectAll.IsEnabled = (callingDataGrid.SelectedItems.Count < callingDataGrid.Items.Count) && callingDataGrid.Items.Count > 0;
      pdgMenuItemUnselectAll.IsEnabled = callingDataGrid.SelectedItems.Count > 0 && callingDataGrid.Items.Count > 0;
      pdgMenuItemShowDamage.IsEnabled = pdgMenuItemShowSpellCasts.IsEnabled = callingDataGrid.SelectedItems.Count > 0;
    }

    private void PlayerDataGridExpander_Loaded(object sender, RoutedEventArgs e)
    {
      Image image = (sender as Image);
      PlayerStats stats = image.DataContext as PlayerStats;
      if (stats != null && CurrentStats.Children.ContainsKey(stats.Name) && (CurrentStats.Children[stats.Name].Count > 1 || stats.Name == DataManager.UNASSIGNED_PET_OWNER))
      {
        var container = playerDataGrid.ItemContainerGenerator.ContainerFromItem(stats) as DataGridRow;
        if (container != null)
        {
          if (container.DetailsVisibility != Visibility.Visible)
          {
            image.Source = EXPAND_BITMAP;
          }
          else
          {
            image.Source = COLLAPSE_BITMAP;
          }
        }
      }
    }

    private void PlayerDataGridExpander_MouseDown(object sender, MouseButtonEventArgs e)
    {
      Image image = (sender as Image);
      PlayerStats stats = image.DataContext as PlayerStats;
      var container = playerDataGrid.ItemContainerGenerator.ContainerFromItem(stats) as DataGridRow;

      if (image != null && container != null)
      {
        if (image.Source == COLLAPSE_BITMAP)
        {
          image.Source = EXPAND_BITMAP;
          container.DetailsVisibility = Visibility.Collapsed;
        }
        else if (image.Source == EXPAND_BITMAP)
        {
          image.Source = COLLAPSE_BITMAP;
          container.DetailsVisibility = Visibility.Visible;
        }
      }
    }

    private void DataGridSelectAll_Click(object sender, RoutedEventArgs e)
    {
      ContextMenu menu = (sender as FrameworkElement).Parent as ContextMenu;
      ThemedDataGrid callingDataGrid = menu.PlacementTarget as ThemedDataGrid;
      callingDataGrid.SelectAll();
    }

    private void DataGridUnselectAll_Click(object sender, RoutedEventArgs e)
    {
      ContextMenu menu = (sender as FrameworkElement).Parent as ContextMenu;
      ThemedDataGrid callingDataGrid = menu.PlacementTarget as ThemedDataGrid;
      callingDataGrid.UnselectAll();
    }

    private void PlayerDataGridSpellCastsByClass_Click(object sender, RoutedEventArgs e)
    {
      MenuItem menuItem = (sender as MenuItem);
      ShowSpellCasts(Utils.GetSelectedPlayerStatsByClass(menuItem.Tag as string, playerDataGrid.Items));
    }

    private void PlayerDataGridShowSpellCasts_Click(object sender, RoutedEventArgs e)
    {
      ContextMenu menu = (sender as FrameworkElement).Parent as ContextMenu;
      ThemedDataGrid callingDataGrid = menu.PlacementTarget as ThemedDataGrid;
      if (callingDataGrid.SelectedItems.Count > 0)
      {
        ShowSpellCasts(callingDataGrid.SelectedItems.Cast<PlayerStats>().ToList());
      }
    }

    private void ShowSpellCasts(List<PlayerStats> selectedStats)
    {
      ThemedDataGrid dataGrid = new ThemedDataGrid();
      dataGrid.AlternatingRowBackground = null;
      dataGrid.AutoGenerateColumns = false;
      dataGrid.RowHeaderWidth = 0;
      dataGrid.Columns.Add(new DataGridTextColumn() { Header = "", Binding = new Binding("Spell") });
      dataGrid.Sorting += (s, e2) =>
      {
        if (e2.Column.Header != null && (e2.Column.Header.ToString() != "Spell"))
        {
          e2.Column.SortDirection = e2.Column.SortDirection ?? ListSortDirection.Ascending;
        }
      };

      List<string> playerList = new List<string>();
      foreach (var stats in selectedStats)
      {
        string name = stats.Name;
        if (CurrentStats.Children.ContainsKey(stats.Name) && CurrentStats.Children[stats.Name].Count > 1)
        {
          name = CurrentStats.Children[stats.Name].First().Name;
        }

        playerList.Add(name);
      }

      ObservableCollection<SpellCountRow> rows = new ObservableCollection<SpellCountRow>();
      dataGrid.ItemsSource = rows;

      busyIcon.Visibility = Visibility.Visible;
      Task.Delay(20).ContinueWith(task =>
      {
        try
        {
          var raidStats = CurrentStats.RaidStats;
          if (raidStats.FirstFightID < int.MaxValue && raidStats.LastFightID > int.MinValue && raidStats.BeginTimes.ContainsKey(raidStats.FirstFightID)
            && raidStats.LastTimes.ContainsKey(raidStats.LastFightID))
          {
            DateTime start = raidStats.BeginTimes[CurrentStats.RaidStats.FirstFightID];
            DateTime end = raidStats.LastTimes[CurrentStats.RaidStats.LastFightID];
            SpellCounts counts = SpellCountBuilder.GetSpellCounts(playerList, start.AddSeconds(-10), end);

            int colCount = 0;
            foreach (string name in counts.SortedPlayers)
            {
              string colBinding = "Values[" + colCount + "]"; // dont use colCount directory since it will change during Dispatch
              int total = counts.TotalCountMap.ContainsKey(name) ? counts.TotalCountMap[name] : 0;

              Dispatcher.InvokeAsync(() =>
              {
                DataGridTextColumn col = new DataGridTextColumn() { Header = name + " = " + total, Binding = new Binding(colBinding) };
                col.CellStyle = Application.Current.Resources["RightAlignGridCellStyle"] as Style;
                col.HeaderStyle = Application.Current.Resources["BrightCenterGridHeaderStyle"] as Style;
                dataGrid.Columns.Add(col);
              });

              Thread.Sleep(5);
              colCount++;
            }

            foreach (var spell in counts.SpellList)
            {
              SpellCountRow row = new SpellCountRow() { Spell = spell, Values = new int[counts.SortedPlayers.Count] };
              for (int i = 0; i < counts.SortedPlayers.Count; i++)
              {
                if (counts.PlayerCountMap.ContainsKey(counts.SortedPlayers[i]))
                {
                  row.Values[i] = counts.PlayerCountMap[counts.SortedPlayers[i]].ContainsKey(spell) ? counts.PlayerCountMap[counts.SortedPlayers[i]][spell] : 0;
                }
              }

              Dispatcher.InvokeAsync(() => rows.Add(row));
              Thread.Sleep(5);
            }
          }

          Dispatcher.InvokeAsync(() => busyIcon.Visibility = Visibility.Hidden);
        }
        catch (Exception err)
        {
          LOG.Error(err);
        }
      });

      if (spellCastsWindow == null || !spellCastsWindow.IsOpen)
      {
        spellCastsWindow = new DocumentWindow(docSite, "spellCastsWindow", "Spell Casts", null, dataGrid);
      }
      else
      {
        spellCastsWindow.Content = dataGrid;
      }

      Utils.OpenWindow(spellCastsWindow);
      spellCastsWindow.MoveToLast();
    }

    private void PlayerDataGridShowDamagByClass_Click(object sender, RoutedEventArgs e)
    {
      MenuItem menuItem = (sender as MenuItem);
      ShowDamage(Utils.GetSelectedPlayerStatsByClass(menuItem.Tag as string, playerDataGrid.Items));
    }

    private void PlayerDataGridShowDamage_Click(object sender, RoutedEventArgs e)
    {
      if (playerDataGrid.SelectedItems.Count > 0)
      {
        ShowDamage(playerDataGrid.SelectedItems.Cast<PlayerStats>().ToList());
      }
    }

    private void ShowDamage(List<PlayerStats> selected)
    {
      ObservableCollection<PlayerStats> list = new ObservableCollection<PlayerStats>();
      playerDamageDataGrid.ItemsSource = list; busyIcon.Visibility = Visibility.Visible;

      Task.Delay(20).ContinueWith(task =>
      {
        foreach (var playerStat in selected)
        {
          if (CurrentStats.Children.ContainsKey(playerStat.Name))
          {
            foreach (var childStat in CurrentStats.Children[playerStat.Name])
            {
              Dispatcher.InvokeAsync(() => list.Add(childStat));
            }
          }
          else
          {
            Dispatcher.InvokeAsync(() => list.Add(playerStat));
          }

          Thread.Sleep(120);
        }

        Dispatcher.InvokeAsync(() => busyIcon.Visibility = Visibility.Hidden);
      });

      if (!damageWindow.IsOpen)
      {
        damageWindow = new DocumentWindow(docSite, "damageWindow", "Damage Breakdown", null, playerDamageParent);
      }

      Utils.OpenWindow(damageWindow);
      damageWindow.MoveToLast();
    }

    // Player DPS Child Grid
    private void PlayerChildrenDataGrid_PrevMouseWheel(object sender, System.Windows.Input.MouseEventArgs e)
    {
      if (!e.Handled)
      {
        e.Handled = true;
        MouseWheelEventArgs wheelArgs = e as MouseWheelEventArgs;
        var newEvent = new MouseWheelEventArgs(wheelArgs.MouseDevice, wheelArgs.Timestamp, wheelArgs.Delta);
        newEvent.RoutedEvent = MouseWheelEvent;
        var container = playerDataGrid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow;
        container.RaiseEvent(newEvent);
      }
    }

    private void PlayerChildrenGrid_RowDetailsVis(object sender, DataGridRowDetailsEventArgs e)
    {
      PlayerStats stats = e.Row.Item as PlayerStats;
      var childrenDataGrid = e.DetailsElement as DataGrid;
      if (stats != null && childrenDataGrid != null && CurrentStats != null && CurrentStats.Children.ContainsKey(stats.Name))
      {
        if (childrenDataGrid.ItemsSource != CurrentStats.Children[stats.Name])
        {
          childrenDataGrid.ItemsSource = CurrentStats.Children[stats.Name];
        }
      }
    }

    private void PlayerDamageGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
      e.Row.IsHitTestVisible = false;
    }

    private void PlayerSubGrid_RowDetailsVis(object sender, DataGridRowDetailsEventArgs e)
    {
      PlayerStats stats = e.Row.Item as PlayerStats;
      var subStatsDataGrid = e.DetailsElement as DataGrid;
      if (stats != null && subStatsDataGrid != null && CurrentStats != null && CurrentStats.SubStats.ContainsKey(stats.Name))
      {
        if (subStatsDataGrid.ItemsSource != CurrentStats.SubStats[stats.Name])
        {
          subStatsDataGrid.ItemsSource = CurrentStats.SubStats[stats.Name];
        }
      }
    }

    // Player DPS Text/Send to EQ Window
    private void CopyToEQ_Click(object sender, RoutedEventArgs e)
    {
      Clipboard.SetText(playerDPSTextBox.Text);
    }

    private void PlayerDPSText_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
      if (!playerDPSTextBox.IsFocused)
      {
        playerDPSTextBox.Focus();
      }
    }

    private void UpdateLoadingProgress()
    {
      if (EQLogReader != null && UpdatingProgress)
      {
        busyIcon.Visibility = Visibility.Visible;
        progressWindow.Title = "Reading Log";
        double percentComplete = EQLogReader.BytesNeededToProcess > 0 ? Math.Min(Convert.ToInt32((double)ProcessedBytes / EQLogReader.BytesNeededToProcess * 100), 100) : 100;
        fileSizeLabel.Content = Math.Ceiling(EQLogReader.FileSize / 1024.0) + " KB";
        bytesProcessedLabel.Content = Math.Ceiling(ProcessedBytes / 1024.0) + " KB";
        completeLabel.Content = percentComplete + "%";
        processedTimeLabel.Content = Math.Round((DateTime.Now - StartLoadTime).TotalSeconds, 1) + " sec";

        if (percentComplete >= 100.0)
        {
          progressWindow.Title = "Monitoring Log";
          percentComplete = 100;
          UpdatingProgress = false;
          completeLabel.Foreground = GOOD_BRUSH;
          busyIcon.Visibility = Visibility.Hidden;
        }
      }
    }

    private void PlayerDPSTextCheckChange(object sender, RoutedEventArgs e)
    {
      NeedDPSTextUpdate = true;
    }

    private void UpdateDPSText()
    {
      if (NeedDPSTextUpdate)
      {
        busyIcon.Visibility = Visibility.Visible;
        DataGrid grid = playerDataGrid;
        Label label = dpsTitle;

        var selected = grid.SelectedItems;
        if (selected != null)
        {
          StatsSummary summary = StatsBuilder.BuildSummary(CurrentStats, selected.Cast<PlayerStats>().ToList(), playerDPSTextDoTotals.IsChecked ?? false, playerDPSTextDoRank.IsChecked ?? false);
          playerDPSTextBox.Text = summary.Title + summary.RankedPlayers;
          playerDPSTextBox.SelectAll();
        }
        else
        {
          playerDPSTextBox.Text = label.Content.ToString();
        }

        busyIcon.Visibility = Visibility.Hidden;
        NeedDPSTextUpdate = false;
      }
    }

    private void UpdateStats()
    {
      if (NeedStatsUpdate && !UpdatingStats)
      {
        bool taskStarted = false;
        UpdatingStats = true;
        var selected = npcDataGrid.SelectedItems;
        if (selected.Count > 0)
        {
          var realItems = selected.Cast<NonPlayer>().Where(item => !item.Name.Contains("Inactivity >")).ToList();
          if (realItems.Count > 0)
          {
            taskStarted = true;
            busyIcon.Visibility = Visibility.Visible;
            new Task(() =>
            {
              CurrentStats = StatsBuilder.BuildTotalStats(realItems);
              Dispatcher.InvokeAsync((() =>
              {
                if (NeedStatsUpdate)
                {
                  dpsTitle.Content = StatsBuilder.BuildTitle(CurrentStats);
                  playerDPSTextBox.Text = dpsTitle.Content.ToString();
                  playerDataGrid.ItemsSource = new ObservableCollection<PlayerStats>(CurrentStats.StatsList);
                  NeedStatsUpdate = false;
                  UpdatingStats = false;

                  if (CurrentStats.StatsList.Count > 0)
                  {
                    foreach (var item in pdgMenuItemShowDamage.Items)
                    {
                      MenuItem menuItem = item as MenuItem;
                      if (CurrentStats.UniqueClasses.ContainsKey(menuItem.Header as string))
                      {
                        menuItem.IsEnabled = true;
                      }
                    }

                    foreach (var item in pdgMenuItemShowSpellCasts.Items)
                    {
                      MenuItem menuItem = item as MenuItem;
                      if (CurrentStats.UniqueClasses.ContainsKey(menuItem.Header as string))
                      {
                        menuItem.IsEnabled = true;
                      }
                    }
                  }
                }
                Dispatcher.InvokeAsync(() => busyIcon.Visibility = Visibility.Hidden);
              }));
            }).Start();
          }
        }

        if (!taskStarted)
        {
          if (playerDataGrid.ItemsSource is ObservableCollection<PlayerStats> list)
          {
            dpsTitle.Content = DPS_LABEL;
            playerDPSTextBox.Text = "";
            list.Clear();
          }

          pdgMenuItemSelectAll.IsEnabled = pdgMenuItemUnselectAll.IsEnabled = pdgMenuItemShowDamage.IsEnabled = pdgMenuItemShowSpellCasts.IsEnabled = false;
          NeedStatsUpdate = false;
          UpdatingStats = false;
        }
      }
    }

    private void OpenLogFile(bool monitorOnly = false, int lastMins = -1)
    {
      try
      {
        MonitorOnly = monitorOnly;

        // WPF doesn't have its own file chooser so use Win32 Version
        Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog();

        // filter to txt files
        dialog.DefaultExt = ".txt";
        dialog.Filter = "eqlog_player_server (.txt)|*.txt";

        // show dialog and read result
        // if null result then dialog was probably canceled
        bool? result = dialog.ShowDialog();
        if (result == true)
        {
          dpsTitle.Content = DPS_LABEL;
          completeLabel.Foreground = BRIGHT_TEXT_BRUSH;
          Title = APP_NAME + " " + VERSION + " -- (" + dialog.FileName + ")";

          NeedStatsUpdate = true;
          UpdatingProgress = true;
          ProcessedBytes = 0;
          StartLoadTime = DateTime.Now;
          CurrentFightID = 0;

          StopProcessors();

          NpcDamageManager = new NpcDamageManager();
          LinePreProcessor = new ActionProcessor("LinePreProcessor", PreProcessLine);
          DamageLineProcessor = new ActionProcessor("DamageLineProcessor", ProcessDamageLine);
          CastLineProcessor = new ActionProcessor("CastLineProcessor", ProcessCastLine);
          CastLineProcessor.LowerPriority();

          string name = "Uknown";
          if (dialog.FileName.Length > 0)
          {
            LOG.Info("Selected Log File: " + dialog.FileName);
            string fileName = dialog.FileName.Substring(dialog.FileName.LastIndexOf("\\") + 1);
            string[] parts = fileName.Split('_');

            if (parts.Length > 1)
            {
              name = parts[1];
            }
          }

          DataManager.Instance.SetPlayerName(name);
          DataManager.Instance.Clear();

          NonPlayersView.Clear();
          npcMenuItemSelectAll.IsEnabled = npcMenuItemUnselectAll.IsEnabled = npcMenuItemSelectFight.IsEnabled = false;

          progressWindow.IsOpen = true;
          EQLogReader = new LogReader(dialog.FileName, FileLoadingCallback, FileLoadingCompleteCallback, monitorOnly, lastMins);
          EQLogReader.Start();
        }
      }
      catch (Exception e)
      {
        LOG.Error(e);
      }
    }

    private void FileLoadingCallback(string line)
    {
      if (line.Length > MIN_LINE_LENGTH)
      {
        LinePreProcessor.AppendToQueue(line);
      }
      else
      {
        Interlocked.Add(ref ProcessedBytes, line.Length + 2);
      }

      if ((LinePreProcessor.QueueSize() + DamageLineProcessor.QueueSize() + CastLineProcessor.QueueSize()) > 200000)
      {
        Thread.Sleep(20);
      }
    }

    private void FileLoadingCompleteCallback()
    {
      LOG.Info("Finished Loading Log File");
      LinePreProcessor.LowerPriority();
      DamageLineProcessor.LowerPriority();
    }

    private void PreProcessLine(object data)
    {
      string line = data as string;
      ProcessLine pline = LineParser.KeepForProcessing(line);
      if (pline != null && pline.State >= 0 && pline.State < 10)
      {
        DamageLineProcessor.AppendToQueue(pline);
      }
      else if (pline != null && pline.State == 10)
      {
        CastLineProcessor.AppendToQueue(pline);
      }
      else
      {
        Interlocked.Add(ref ProcessedBytes, line.Length + 2);
      }
    }

    private void ProcessCastLine(object data)
    {
      ProcessLine pline = data as ProcessLine;
      if (pline != null)
      {
        try
        {
          SpellCast cast = LineParser.ParseSpellCast(pline);
          if (cast != null)
          {
            DataManager.Instance.AddSpellCast(cast);
          }

          Interlocked.Add(ref ProcessedBytes, pline.Line.Length + 2);
        }
        catch (Exception e)
        {
          LOG.Error(e);
        }
      }
    }

    private void ProcessDamageLine(object data)
    {
      ProcessLine pline = data as ProcessLine;
      if (pline != null)
      {
        if (pline.CurrentTime == DateTime.MinValue)
        {
          Interlocked.Add(ref ProcessedBytes, pline.Line.Length + 2);
          return; // abort
        }

        try
        {
          switch (pline.State)
          {
            case 0:
              // check for damage
              DamageRecord record = LineParser.ParseDamage(pline.ActionPart);
              if (record != null)
              {
                if (NpcDamageManager.LastUpdateTime != DateTime.MinValue)
                {
                  TimeSpan diff = pline.CurrentTime.Subtract(NpcDamageManager.LastUpdateTime);
                  if (diff.TotalSeconds > 120)
                  {
                    NonPlayer divider = new NonPlayer() { FightID = -1, BeginTimeString = NonPlayer.BREAK_TIME, Name = Utils.FormatTimeSpan(diff) };
                    Dispatcher.InvokeAsync(() =>
                    {
                      CurrentFightID++;
                      NonPlayersView.Add(divider);
                    });
                  }
                }

                NpcDamageManager.AddOrUpdateNpc(record, pline.CurrentTime, pline.TimeString.Substring(4, 15));
              }
              break;
            case 1:
              // check slain
              LineParser.CheckForSlain(pline);
              break;
            case 2:
              LineParser.CheckForHeal(pline);
              break;
          }
        }
        catch (Exception e)
        {
          LOG.Error(e);
        }
      }

      Interlocked.Add(ref ProcessedBytes, pline.Line.Length + 2);
    }

    private void PlayerDPSTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
      if (playerDPSTextBox.Text == "")
      {
        copyToEQButton.IsEnabled = false;
        copyToEQButton.Foreground = LIGHTER_BRUSH;
        sharePlayerDPSLabel.Text = SHARE_DPS_LABEL;
        sharePlayerDPSLabel.Foreground = BRIGHT_TEXT_BRUSH;
        sharePlayerDPSWarningLabel.Text = playerDPSTextBox.Text.Length + "/" + 509;
        sharePlayerDPSWarningLabel.Visibility = Visibility.Hidden;
      }
      else if (playerDPSTextBox.Text.Length > 509)
      {
        copyToEQButton.IsEnabled = false;
        copyToEQButton.Foreground = LIGHTER_BRUSH;
        sharePlayerDPSLabel.Text = SHARE_DPS_TOO_BIG_LABEL;
        sharePlayerDPSLabel.Foreground = WARNING_BRUSH;
        sharePlayerDPSWarningLabel.Text = playerDPSTextBox.Text.Length + "/" + 509;
        sharePlayerDPSWarningLabel.Foreground = WARNING_BRUSH;
        sharePlayerDPSWarningLabel.Visibility = Visibility.Visible;
      }
      else if (playerDPSTextBox.Text.Length > 0)
      {
        copyToEQButton.IsEnabled = true;
        copyToEQButton.Foreground = BRIGHT_TEXT_BRUSH;
        var count = playerDataGrid.SelectedItems.Count;
        string players = count == 1 ? "Player" : "Players";
        sharePlayerDPSLabel.Text = String.Format("{0} {1} Selected", count, players);
        sharePlayerDPSLabel.Foreground = BRIGHT_TEXT_BRUSH;
        sharePlayerDPSWarningLabel.Text = playerDPSTextBox.Text.Length + " / " + 509;
        sharePlayerDPSWarningLabel.Foreground = GOOD_BRUSH;
        sharePlayerDPSWarningLabel.Visibility = Visibility.Visible;
      }
    }

    private void StopProcessors()
    {
      if (CastLineProcessor != null)
      {
        CastLineProcessor.Stop();
      }

      if (DamageLineProcessor != null)
      {
        DamageLineProcessor.Stop();
      }

      if (LinePreProcessor != null)
      {
        LinePreProcessor.Stop();
      }

      if (EQLogReader != null)
      {
        EQLogReader.Stop();
      }
    }
  }
}
