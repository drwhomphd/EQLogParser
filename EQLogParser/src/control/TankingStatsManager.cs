﻿
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace EQLogParser
{
  class TankingStatsManager : ISummaryBuilder
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    internal static TankingStatsManager Instance = new TankingStatsManager();

    internal event EventHandler<DataPointEvent> EventsUpdateDataPoint;
    internal event EventHandler<StatsGenerationEvent> EventsGenerationStatus;

    internal Dictionary<string, byte> NpcNames = new Dictionary<string, byte>();
    internal List<List<ActionBlock>> TankingGroups = new List<List<ActionBlock>>();

    private const int TANK_OFFSET = 10; // additional # of seconds to count

    private PlayerStats RaidTotals;
    private List<NonPlayer> Selected;
    private string Title;

    internal TankingStatsManager()
    {
      DataManager.Instance.EventsClearedActiveData += (object sender, bool e) =>
      {
        TankingGroups.Clear();
        RaidTotals = null;
        Selected = null;
        Title = "";
      };
    }

    internal void BuildTotalStats(TankingStatsOptions options)
    {
      Selected = options.Npcs;
      Title = options.Name;

      try
      {
        FireNewStatsEvent(options);

        RaidTotals = StatsUtil.CreatePlayerStats(Labels.RAID);
        TankingGroups.Clear();
        NpcNames.Clear();

        Selected.ForEach(npc =>
        {
          StatsUtil.UpdateTimeDiffs(RaidTotals, npc, TANK_OFFSET);
          NpcNames[npc.Name] = 1;
        });

        RaidTotals.TotalSeconds = RaidTotals.TimeDiffs.Sum();

        if (RaidTotals.BeginTimes.Count > 0 && RaidTotals.BeginTimes.Count == RaidTotals.LastTimes.Count)
        {
          for (int i = 0; i < RaidTotals.BeginTimes.Count; i++)
          {
            TankingGroups.Add(DataManager.Instance.GetDefendDamageDuring(RaidTotals.BeginTimes[i], RaidTotals.LastTimes[i]));
          }

          ComputeTankingStats(options);
        }
        else
        {
          FireNoDataEvent(options);
        }
      }
      catch (ArgumentNullException ne)
      {
        LOG.Error(ne);
      }
      catch (NullReferenceException nr)
      {
        LOG.Error(nr);
      }
      catch (ArgumentOutOfRangeException aor)
      {
        LOG.Error(aor);
      }
      catch (ArgumentException ae)
      {
        LOG.Error(ae);
      }
    }

    internal void RebuildTotalStats(TankingStatsOptions options)
    {
      FireNewStatsEvent(options);
      ComputeTankingStats(options);
    }

    internal void FireSelectionEvent(TankingStatsOptions options, List<PlayerStats> selected)
    {
      if (options.RequestChartData)
      {
        // send update
        DataPointEvent de = new DataPointEvent() { Action = "SELECT", Selected = selected };
        EventsUpdateDataPoint?.Invoke(TankingGroups, de);
      }
    }

    internal void FireUpdateEvent(TankingStatsOptions options, List<PlayerStats> selected = null)
    {
      if (options.RequestChartData)
      {
        // send update
        DataPointEvent de = new DataPointEvent() { Action = "UPDATE", Selected = selected, Iterator = new TankGroupCollection(TankingGroups) };
        EventsUpdateDataPoint?.Invoke(TankingGroups, de);
      }
    }

    private void FireCompletedEvent(TankingStatsOptions options, CombinedTankStats combined)
    {
      if (options.RequestSummaryData)
      {
        // generating new stats
        EventsGenerationStatus?.Invoke(this, new StatsGenerationEvent()
        {
          Type = Labels.TANKPARSE,
          State = "COMPLETED",
          CombinedStats = combined
        });
      }
    }

    private void FireNewStatsEvent(TankingStatsOptions options)
    {
      if (options.RequestSummaryData)
      {
        // generating new stats
        EventsGenerationStatus?.Invoke(this, new StatsGenerationEvent() { Type = Labels.TANKPARSE, State = "STARTED" });
      }
    }

    private void FireNoDataEvent(TankingStatsOptions options)
    {
      if (options.RequestSummaryData)
      {
        // nothing to do
        EventsGenerationStatus?.Invoke(this, new StatsGenerationEvent() { Type = Labels.TANKPARSE, State = "NONPC" });
      }

      if (options.RequestChartData)
      {
        // send update
        DataPointEvent de = new DataPointEvent() { Action = "CLEAR" };
        EventsUpdateDataPoint?.Invoke(TankingGroups, de);
      }
    }

    internal bool IsValidDamage(DamageRecord record)
    {
      bool valid = false;

      if (record != null && NpcNames.ContainsKey(record.Attacker) && !DataManager.Instance.IsProbablyNotAPlayer(record.Defender))
      {
        valid = true;
      }

      return valid;
    }

    private void ComputeTankingStats(TankingStatsOptions options)
    {
      CombinedTankStats combined = null;
      Dictionary<string, PlayerStats> individualStats = new Dictionary<string, PlayerStats>();

      // always start over
      RaidTotals.Total = 0;

      try
      {
        FireUpdateEvent(options);

        if (options.RequestSummaryData)
        {
          TankingGroups.ForEach(group =>
          {
            // keep track of time range as well as the players that have been updated
            Dictionary<string, PlayerSubStats> allStats = new Dictionary<string, PlayerSubStats>();

            group.ForEach(block =>
            {
              block.Actions.ForEach(action =>
              {
                DamageRecord record = action as DamageRecord;

                if (IsValidDamage(record))
                {
                  RaidTotals.Total += record.Total;
                  PlayerStats stats = StatsUtil.CreatePlayerStats(individualStats, record.Defender);

                  StatsUtil.UpdateStats(stats, record, block.BeginTime);
                  allStats[record.Defender] = stats;
                }
              });
            });

            Parallel.ForEach(allStats.Values, stats =>
            {
              stats.TotalSeconds += stats.LastTime - stats.BeginTime + 1;
              stats.BeginTime = double.NaN;
            });
          });

          RaidTotals.DPS = (long)Math.Round(RaidTotals.Total / RaidTotals.TotalSeconds, 2);
          Parallel.ForEach(individualStats.Values, stats => StatsUtil.UpdateCalculations(stats, RaidTotals));

          combined = new CombinedTankStats
          {
            RaidStats = RaidTotals,
            UniqueClasses = new Dictionary<string, byte>(),
            StatsList = individualStats.Values.AsParallel().OrderByDescending(item => item.Total).ToList(),
            TargetTitle = (Selected.Count > 1 ? "Combined (" + Selected.Count + "): " : "") + Title,
            TimeTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TIME_FORMAT, RaidTotals.TotalSeconds),
            TotalTitle = string.Format(CultureInfo.CurrentCulture, StatsUtil.TOTAL_FORMAT, StatsUtil.FormatTotals(RaidTotals.Total), " Tanked ", StatsUtil.FormatTotals(RaidTotals.DPS))
          };

          combined.FullTitle = StatsUtil.FormatTitle(combined.TargetTitle, combined.TimeTitle, combined.TotalTitle);
          combined.ShortTitle = StatsUtil.FormatTitle(combined.TargetTitle, combined.TimeTitle, "");

          for (int i = 0; i < combined.StatsList.Count; i++)
          {
            combined.StatsList[i].Rank = Convert.ToUInt16(i + 1);
            combined.UniqueClasses[combined.StatsList[i].ClassName] = 1;
          }
        }
      }
      catch (ArgumentNullException ane)
      {
        LOG.Error(ane);
      }
      catch (NullReferenceException nre)
      {
        LOG.Error(nre);
      }
      catch (ArgumentOutOfRangeException aro)
      {
        LOG.Error(aro);
      }

      FireCompletedEvent(options, combined);
    }


    public StatsSummary BuildSummary(CombinedStats currentStats, List<PlayerStats> selected, bool showTotals, bool rankPlayers)
    {
      List<string> list = new List<string>();

      string title = "";
      string details = "";

      if (currentStats != null)
      {
        if (selected != null)
        {
          foreach (PlayerStats stats in selected.OrderByDescending(item => item.Total))
          {
            string playerFormat = rankPlayers ? string.Format(CultureInfo.CurrentCulture, StatsUtil.PLAYER_RANK_FORMAT, stats.Rank, stats.Name) : string.Format(CultureInfo.CurrentCulture, StatsUtil.PLAYER_FORMAT, stats.Name);
            string damageFormat = string.Format(CultureInfo.CurrentCulture, StatsUtil.TOTAL_ONLY_FORMAT, StatsUtil.FormatTotals(stats.Total));
            list.Add(playerFormat + damageFormat + " ");
          }
        }

        details = list.Count > 0 ? ", " + string.Join(" | ", list) : "";
        title = StatsUtil.FormatTitle(currentStats.TargetTitle, currentStats.TimeTitle, showTotals ? currentStats.TotalTitle : "");
      }

      return new StatsSummary() { Title = title, RankedPlayers = details, };
    }
  }
}
