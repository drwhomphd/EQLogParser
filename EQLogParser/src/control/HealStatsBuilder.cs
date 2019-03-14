﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace EQLogParser
{
  class HealStatsBuilder : StatsBuilder
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    internal static event EventHandler<DataPointEvent> EventsUpdateDataPoint;
    internal static List<List<TimedAction>> HealGroups = new List<List<TimedAction>>();

    private const int HEAL_OFFSET = 5; // additional # of seconds to count hilling after last damage is seen
    private const string UNKNOWN_SPELL = "Unknown Spell";
    private const string UNKNOWN_PLAYER = "Unknown Player";

    internal static StatsSummary BuildSummary(CombinedStats currentStats, List<PlayerStats> selected, bool showTotals, bool rankPlayers)
    {
      List<string> list = new List<string>();

      string title = "";
      string details = "";
      string shortTitle = "";

      if (currentStats != null)
      {
        if (selected != null)
        {
          foreach (PlayerStats stats in selected.OrderByDescending(item => item.Total))
          {
            string playerFormat = rankPlayers ? string.Format(PLAYER_RANK_FORMAT, stats.Rank, stats.Name) : string.Format(PLAYER_FORMAT, stats.Name);
            string damageFormat = string.Format(TOTAL_ONLY_FORMAT, Helpers.FormatDamage(stats.Total));
            list.Add(playerFormat + damageFormat + " ");
          }
        }

        details = list.Count > 0 ? ", " + string.Join(" | ", list) : "";
        title = BuildTitle(currentStats, showTotals);
        shortTitle = BuildTitle(currentStats, false);
      }

      return new StatsSummary() { Title = title, RankedPlayers = details, ShortTitle = shortTitle };
    }

    internal static CombinedHealStats BuildTotalStats(string title, List<NonPlayer> selected)
    {
      CombinedHealStats combined = new CombinedHealStats() { UniqueClasses = new Dictionary<string, byte>() };

      try
      {
        HealGroups.Clear();

        PlayerStats raidTotals = CreatePlayerStats(RAID_PLAYER);
        selected.ForEach(npc => UpdateTimeDiffs(raidTotals, npc, HEAL_OFFSET));
        raidTotals.TotalSeconds = raidTotals.TimeDiffs.Sum();
        combined.RaidStats = raidTotals;

        if (raidTotals.BeginTimes.Count > 0 && raidTotals.BeginTimes.Count == raidTotals.LastTimes.Count)
        {
          Dictionary<string, PlayerStats> individualStats = new Dictionary<string, PlayerStats>();

          
          for (int i = 0; i < raidTotals.BeginTimes.Count; i++)
          {
            HealGroups.Add(DataManager.Instance.GetHealsDuring(raidTotals.BeginTimes[i], raidTotals.LastTimes[i]));
          }

          // send update
          DataPointEvent de = new DataPointEvent() { EventType = "UPDATE" };
          EventsUpdateDataPoint?.Invoke(HealGroups, de);

          HealGroups.ForEach(records =>
          {
            // keep track of time range as well as the players that have been updated
            Dictionary<string, PlayerSubStats> allStats = new Dictionary<string, PlayerSubStats>();

            records.ForEach(timedAction =>
            {
              HealRecord record = timedAction as HealRecord;
              if (!DataManager.Instance.IsProbablyNotAPlayer(record.Healed))
              {
                raidTotals.Total += record.Total;
                PlayerStats stats = CreatePlayerStats(individualStats, record.Healer);

                UpdateStats(stats, record);
                allStats[record.Healer] = stats;

                var spellStatName = record.SubType ?? UNKNOWN_SPELL;
                PlayerSubStats spellStats = CreatePlayerSubStats(stats.SubStats, spellStatName, record.Type);
                UpdateStats(spellStats, record);
                allStats[stats.Name + "=" + spellStatName] = spellStats;

                var healedStatName = record.Healed;
                stats.SubStats2 = new Dictionary<string, PlayerSubStats>();
                PlayerSubStats healedStats = CreatePlayerSubStats(stats.SubStats2, healedStatName, record.Type);
                UpdateStats(healedStats, record);
                allStats[stats.Name + "=" + healedStatName] = healedStats;
              }
            });

            Parallel.ForEach(allStats.Values, stats =>
            {
              stats.TotalSeconds += stats.LastTime.Subtract(stats.BeginTime).TotalSeconds + 1;
              stats.BeginTime = DateTime.MinValue;
            });
          });

          Parallel.ForEach(individualStats.Values, stats => UpdateCalculations(stats, raidTotals));

          raidTotals.DPS = (long) Math.Round(raidTotals.Total / raidTotals.TotalSeconds, 2);
          combined.StatsList = individualStats.Values.AsParallel().OrderByDescending(item => item.Total).ToList();
          combined.TargetTitle = (selected.Count > 1 ? "Combined (" + selected.Count + "): " : "") + title;
          combined.TimeTitle = string.Format(TIME_FORMAT, raidTotals.TotalSeconds);
          combined.TotalTitle = string.Format(TOTAL_FORMAT, Helpers.FormatDamage(raidTotals.Total), " Heals ", Helpers.FormatDamage(raidTotals.DPS));

          for (int i = 0; i < combined.StatsList.Count; i++)
          {
            combined.StatsList[i].Rank = i + 1;
            combined.UniqueClasses[combined.StatsList[i].ClassName] = 1;
          }
        }
      }
      catch (Exception e)
      {
        LOG.Error(e);
      }

      return combined;
    }
  }
}
