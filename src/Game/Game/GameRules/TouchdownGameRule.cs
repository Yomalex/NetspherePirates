﻿using System;
using System.IO;
using System.Linq;
using Netsphere.Network.Data.GameRule;
using Netsphere.Network.Message.GameRule;

namespace Netsphere.Game.GameRules
{
    internal sealed class TouchdownGameRule : GameRuleBase
    {
        private static readonly TimeSpan TouchdownWaitTime = TimeSpan.FromSeconds(12);

        private readonly TouchdownAssistHelper _touchdownAssistHelper = new TouchdownAssistHelper();
        private TimeSpan _touchdownTime;

        public override GameRule GameRule => GameRule.Touchdown;
        public override Briefing Briefing { get; }
        public bool IsInTouchdown { get; private set; }

        public TouchdownGameRule(Room room)
            : base(room)
        {
            Briefing = new Briefing(this);

            StateMachine.Configure(GameRuleState.Waiting)
                .PermitIf(GameRuleStateTrigger.StartGame, GameRuleState.FirstHalf, CanStartGame);

            StateMachine.Configure(GameRuleState.FirstHalf)
                .SubstateOf(GameRuleState.Playing)
                .Permit(GameRuleStateTrigger.StartHalfTime, GameRuleState.EnteringHalfTime)
                .Permit(GameRuleStateTrigger.StartResult, GameRuleState.EnteringResult);

            StateMachine.Configure(GameRuleState.EnteringHalfTime)
                .SubstateOf(GameRuleState.Playing)
                .Permit(GameRuleStateTrigger.StartHalfTime, GameRuleState.HalfTime)
                .Permit(GameRuleStateTrigger.StartResult, GameRuleState.EnteringResult);

            StateMachine.Configure(GameRuleState.HalfTime)
                .SubstateOf(GameRuleState.Playing)
                .Permit(GameRuleStateTrigger.StartSecondHalf, GameRuleState.SecondHalf)
                .Permit(GameRuleStateTrigger.StartResult, GameRuleState.EnteringResult);

            StateMachine.Configure(GameRuleState.SecondHalf)
                .SubstateOf(GameRuleState.Playing)
                .Permit(GameRuleStateTrigger.StartResult, GameRuleState.EnteringResult);

            StateMachine.Configure(GameRuleState.EnteringResult)
                .SubstateOf(GameRuleState.Playing)
                .Permit(GameRuleStateTrigger.StartResult, GameRuleState.Result);

            StateMachine.Configure(GameRuleState.Result)
                .SubstateOf(GameRuleState.Playing)
                .Permit(GameRuleStateTrigger.EndGame, GameRuleState.Waiting)
                .OnEntry(UpdatePlayerStats);
        }

        public override void Initialize()
        {
            var teamMgr = Room.TeamManager;
            teamMgr.Add(Team.Alpha, (uint)(Room.Options.MatchKey.PlayerLimit / 2), (uint)(Room.Options.MatchKey.SpectatorLimit / 2));
            teamMgr.Add(Team.Beta, (uint)(Room.Options.MatchKey.PlayerLimit / 2), (uint)(Room.Options.MatchKey.SpectatorLimit / 2));

            base.Initialize();
        }

        public override void Cleanup()
        {
            var teamMgr = Room.TeamManager;
            teamMgr.Remove(Team.Alpha);
            teamMgr.Remove(Team.Beta);

            base.Cleanup();
        }

        public override void PlayerLeft(object room, RoomPlayerEventArgs e)
        {
            if (StateMachine.IsInState(GameRuleState.FirstHalf)
                || StateMachine.IsInState(GameRuleState.HalfTime)
                || StateMachine.IsInState(GameRuleState.SecondHalf))
                e.Player.TouchDown.Loss++;
            base.PlayerLeft(room, e);
        }

        public override void Update(TimeSpan delta)
        {
            base.Update(delta);

            var teamMgr = Room.TeamManager;

            if (StateMachine.IsInState(GameRuleState.Playing) &&
                !StateMachine.IsInState(GameRuleState.EnteringResult) &&
                !StateMachine.IsInState(GameRuleState.Result) &&
                RoundTime >= TimeSpan.FromSeconds(5)) // Let the round run for at least 5 seconds - Fixes StartResult trigger on game start(race condition)
            {
                // Still have enough players?
                var min = teamMgr.Values.Min(team =>
                team.Values.Count(plr =>
                    plr.RoomInfo.State != PlayerState.Lobby &&
                    plr.RoomInfo.State != PlayerState.Spectating));
                if (min == 0)
                    StateMachine.Fire(GameRuleStateTrigger.StartResult);

                var isFirstHalf = StateMachine.IsInState(GameRuleState.FirstHalf);
                var isSecondHalf = StateMachine.IsInState(GameRuleState.SecondHalf);
                if (isFirstHalf || isSecondHalf)
                {
                    var scoreLimit = isFirstHalf ? Room.Options.ScoreLimit / 2 : Room.Options.ScoreLimit;
                    var trigger = isFirstHalf ? GameRuleStateTrigger.StartHalfTime : GameRuleStateTrigger.StartResult;

                    // Did we reach ScoreLimit?
                    if (teamMgr.Values.Any(team => team.Score >= scoreLimit))
                        StateMachine.Fire(trigger);

                    // Did we reach round limit?
                    var roundTimeLimit = TimeSpan.FromMilliseconds(Room.Options.TimeLimit.TotalMilliseconds / 2);
                    if (RoundTime >= roundTimeLimit)
                        StateMachine.Fire(trigger);
                }

                if (IsInTouchdown)
                {
                    _touchdownTime += delta;
                    if (!StateMachine.IsInState(GameRuleState.EnteringHalfTime) && !StateMachine.IsInState(GameRuleState.HalfTime) &&
                        !StateMachine.IsInState(GameRuleState.EnteringResult) && !StateMachine.IsInState(GameRuleState.Result))
                    {
                        if (_touchdownTime >= TouchdownWaitTime)
                        {
                            IsInTouchdown = false;
                            _touchdownTime = TimeSpan.Zero;
                            Room.Broadcast(new SEventMessageAckMessage(GameEventMessage.ResetRound, 0, 0, 0, ""));
                        }
                    }
                    else
                    {
                        IsInTouchdown = false;
                    }
                }
            }
        }

        public override PlayerRecord GetPlayerRecord(Player plr)
        {
            return new TouchdownPlayerRecord(plr);
        }

        public void OnScoreOffense(Player killer, Player assist, Player target, AttackAttribute attackAttribute)
        {
            if (IsInTouchdown)
                return;

            GetRecord(killer).OffenseScore++;
            killer.TouchDown.Offense++;
            if (assist != null)
            {
                GetRecord(assist).OffenseAssistScore++;
                assist.TouchDown.OffenseAssist++;
            }

            if (assist != null)
                Room.Broadcast(new SScoreOffenseAssistAckMessage(new ScoreAssistDto(killer.RoomInfo.PeerId, assist.RoomInfo.PeerId, target.RoomInfo.PeerId, attackAttribute)));
            else
                Room.Broadcast(new SScoreOffenseAckMessage(new ScoreDto(killer.RoomInfo.PeerId, target.RoomInfo.PeerId, attackAttribute)));
        }

        public void OnScoreDefense(Player killer, Player assist, Player target, AttackAttribute attackAttribute)
        {
            if (IsInTouchdown)
                return;

            GetRecord(killer).DefenseScore++;
            killer.TouchDown.Defense++;
            if (assist != null)
            {
                GetRecord(assist).DefenseAssistScore++;
                assist.TouchDown.DefenseAssist++;
            }

            if (assist != null)
                Room.Broadcast(new SScoreDefenseAssistAckMessage(new ScoreAssistDto(killer.RoomInfo.PeerId, assist.RoomInfo.PeerId, target.RoomInfo.PeerId, attackAttribute)));
            else
                Room.Broadcast(new SScoreDefenseAckMessage(new ScoreDto(killer.RoomInfo.PeerId, target.RoomInfo.PeerId, attackAttribute)));
        }

        public void OnScoreRebound(Player newPlr, Player oldPlr)
        {
            if (IsInTouchdown)
                return;

            if (oldPlr != null)
                _touchdownAssistHelper.Update(oldPlr);

            if (newPlr != null)
            {
                GetRecord(newPlr).OffenseReboundScore++;
                newPlr.TouchDown.OffenseRebound++;
            }

            Room.Broadcast(new SScoreReboundAckMessage(newPlr?.RoomInfo.PeerId ?? 0, oldPlr?.RoomInfo.PeerId ?? 0));
        }

        public void OnScoreGoal(Player plr)
        {
            IsInTouchdown = true;

            Player assist = null;
            if (_touchdownAssistHelper.IsAssist(plr))
            {
                assist = _touchdownAssistHelper.LastPlayer;
                GetRecord(assist).TDAssistScore++;
                assist.TouchDown.TDAssist++;
            }

            plr.RoomInfo.Team.Score++;
            GetRecord(plr).TDScore++;
            plr.TouchDown.TD++;

            if (assist != null)
                Room.Broadcast(new SScoreGoalAssistAckMessage(plr.RoomInfo.PeerId, assist.RoomInfo.PeerId));
            else
                Room.Broadcast(new SScoreGoalAckMessage(plr.RoomInfo.PeerId));

            var halfTime = TimeSpan.FromSeconds(Room.Options.TimeLimit.TotalSeconds / 2);
            var diff = halfTime - RoundTime;
            if (diff <= TimeSpan.FromSeconds(10)) // ToDo use const
                return;

            Room.Broadcast(new SEventMessageAckMessage(GameEventMessage.NextRoundIn, (ulong)TouchdownWaitTime.TotalMilliseconds, 0, 0, ""));
            _touchdownTime = TimeSpan.Zero;
        }

        public override void OnScoreHeal(Player plr)
        {
            if (IsInTouchdown)
                return;

            GetRecord(plr).HealScore++;
            plr.TouchDown.TDHeal++;
            base.OnScoreHeal(plr);
        }

        public override void OnScoreKill(Player killer, Player assist, Player target, AttackAttribute attackAttribute)
        {
            if (IsInTouchdown)
                return;

            killer.TouchDown.Kill++;
            if (assist != null)
                assist.TouchDown.KillAssists++;

            base.OnScoreKill(killer, assist, target, attackAttribute);
        }

        public override void OnScoreTeamKill(Player killer, Player target, AttackAttribute attackAttribute)
        {
            if (IsInTouchdown)
                return;

            base.OnScoreTeamKill(killer, target, attackAttribute);
        }

        public override void OnScoreSuicide(Player plr)
        {
            if (IsInTouchdown)
                return;

            base.OnScoreSuicide(plr);
        }

        private bool CanStartGame()
        {
            if (!StateMachine.IsInState(GameRuleState.Waiting))
                return false;

            var teams = Room.TeamManager.Values.ToArray();
            if (teams.Any(team => team.Count == 0)) // Do we have enough players?
                return false;

            // Is atleast one player per team ready?
            return teams.All(team => team.Players.Any(plr => plr.RoomInfo.IsReady || Room.Master == plr));
        }

        private static TouchdownPlayerRecord GetRecord(Player plr)
        {
            return (TouchdownPlayerRecord)plr.RoomInfo.Stats;
        }

        private void UpdatePlayerStats()
        {
            var WinTeam = Room
                .TeamManager
                .PlayersPlaying
                .Aggregate(
                    (highestTeam, player) =>
                    (highestTeam == null || player.RoomInfo.Team.Score > highestTeam.RoomInfo.Team.Score) ?
                    player : highestTeam).RoomInfo.Team;

            foreach (var plr in Room.TeamManager.PlayersPlaying)
            {
                if (plr.RoomInfo.Team == WinTeam)
                    plr.TouchDown.Won++;
                else
                    plr.TouchDown.Loss++;
            }
        }
    }

    internal class TouchdownPlayerRecord : PlayerRecord
    {
        public override uint TotalScore => GetTotalScore();

        public uint TDScore { get; set; }
        public uint TDAssistScore { get; set; }
        public uint OffenseScore { get; set; }
        public uint OffenseAssistScore { get; set; }
        public uint DefenseScore { get; set; }
        public uint DefenseAssistScore { get; set; }
        public uint HealScore { get; set; }
        public uint Unk1 { get; set; }
        public uint Unk2 { get; set; }
        public uint Unk3 { get; set; }
        public uint OffenseReboundScore { get; set; }
        public uint Unk4 { get; set; } // increases total score x*4
        public uint Unk5 { get; set; }

        public TouchdownPlayerRecord(Player plr)
            : base(plr)
        { }

        public override void Serialize(BinaryWriter w, bool isResult)
        {
            base.Serialize(w, isResult);

            w.Write(TDScore);
            w.Write(TDAssistScore);
            w.Write(Kills);
            w.Write(KillAssists);
            w.Write(OffenseScore);
            w.Write(OffenseAssistScore);
            w.Write(DefenseScore);
            w.Write(DefenseAssistScore);
            w.Write(HealScore);
            w.Write(Unk1);
            w.Write(Unk2);
            w.Write(Unk3);
            w.Write(OffenseReboundScore);
            w.Write(Unk4);
            w.Write(Unk5);
        }

        public override void Reset()
        {
            base.Reset();
            TDScore = 0;
            TDAssistScore = 0;
            OffenseScore = 0;
            OffenseAssistScore = 0;
            DefenseScore = 0;
            DefenseAssistScore = 0;
            HealScore = 0;
            OffenseReboundScore = 0;

            Unk1 = 0;
            Unk2 = 0;
            Unk3 = 0;
            Unk4 = 0;
            Unk5 = 0;
        }

        private uint GetTotalScore()
        {
            return 5 * (TDScore * 2 + TDAssistScore) + 2 * (2 * (OffenseScore + DefenseScore) + OffenseAssistScore + DefenseAssistScore + HealScore + OffenseReboundScore + Kills) + KillAssists;
            //return TDScore * 10 + TDAssistScore * 5
            //    + Kills * 2 + KillAssists
            //    + OffenseScore * 4 + OffenseAssistScore * 2
            //    + DefenseScore * 4 + DefenseAssistScore * 2
            //    + HealScore * 2
            //    + OffenseReboundScore * 2;
        }

        public override uint GetExpGain(out uint bonusExp)
        {
            return GetExpGain(Config.Instance.Game.TouchdownExpRates, out bonusExp);
        }

        public override uint GetPenGain(out uint bonusPen)
        {
            return GetPenGain(Config.Instance.Game.TouchdownExpRates, out bonusPen);
        }
    }

    internal class TouchdownAssistHelper
    {
        private static readonly TimeSpan TouchdownAssistTimer = TimeSpan.FromSeconds(10);

        public DateTime LastTime { get; set; }
        public Player LastPlayer { get; set; }

        public void Update(Player plr)
        {
            LastTime = DateTime.Now;
            LastPlayer = plr;
        }

        public bool IsAssist(Player plr)
        {
            if (LastPlayer == null)
                return false;

            if (plr.RoomInfo.Team != LastPlayer.RoomInfo.Team)
                return false;

            return DateTime.Now - LastTime < TouchdownAssistTimer;
        }
    }
}
