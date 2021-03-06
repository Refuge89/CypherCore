﻿/*
 * Copyright (C) 2012-2017 CypherCore <http://github.com/CypherCore>
 * 
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */

using Framework.Constants;
using Game.DataStorage;
using Game.Entities;
using Game.Groups;
using Game.Scripting;
using Game.Spells;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Game.AI
{
    public class SmartAI : CreatureAI
    {
        const int SMART_ESCORT_MAX_PLAYER_DIST = 50;
        const int SMART_MAX_AID_DIST = SMART_ESCORT_MAX_PLAYER_DIST / 2;

        public SmartAI(Creature creature) : base(creature)
        {
            // copy script to local (protection for table reload)

            mWayPoints = null;
            mEscortState = SmartEscortState.None;
            mCurrentWPID = 0;//first wp id is 1 !!
            mWPReached = false;
            mWPPauseTimer = 0;
            mLastWP = null;

            mCanRepeatPath = false;

            // spawn in run mode
            creature.SetWalk(false);
            mRun = false;

            mLastOOCPos = creature.GetPosition();

            mCanAutoAttack = true;
            mCanCombatMove = true;

            mForcedPaused = false;
            mLastWPIDReached = 0;

            mEscortQuestID = 0;

            mDespawnTime = 0;
            mDespawnState = 0;

            mEscortInvokerCheckTimer = 1000;
            mFollowGuid = ObjectGuid.Empty;
            mFollowDist = 0;
            mFollowAngle = 0;
            mFollowCredit = 0;
            mFollowArrivedEntry = 0;
            mFollowCreditType = 0;
            mInvincibilityHpLevel = 0;
        }

        bool IsAIControlled()
        {
            if (me.IsControlledByPlayer())
                return false;
            if (mIsCharmed)
                return false;
            return true;
        }

        void UpdateDespawn(uint diff)
        {
            if (mDespawnState <= 1 || mDespawnState > 3)
                return;

            if (mDespawnTime < diff)
            {
                if (mDespawnState == 2)
                {
                    me.SetVisible(false);
                    mDespawnTime = 5000;
                    mDespawnState++;
                }
                else
                    me.DespawnOrUnsummon(0, TimeSpan.FromSeconds(mRespawnTime));
            }
            else
                mDespawnTime -= diff;
        }

        public override void Reset()
        {
            if (!HasEscortState(SmartEscortState.Escorting))//dont mess up escort movement after combat
                SetRun(mRun);
            GetScript().OnReset();
        }

        SmartPath GetNextWayPoint()
        {
            if (mWayPoints == null || mWayPoints.Empty())
                return null;

            mCurrentWPID++;
            var path = mWayPoints.LookupByKey(mCurrentWPID);
            if (path != null)
            {
                mLastWP = path;
                if (mLastWP.id != mCurrentWPID)
                {
                    Log.outError(LogFilter.Server, "SmartAI.GetNextWayPoint: Got not expected waypoint id {0}, expected {1}", mLastWP.id, mCurrentWPID);
                }
                return path;
            }
            return null;
        }

        public void StartPath(bool run = false, uint path = 0, bool repeat = false, Unit invoker = null)
        {
            if (me.IsInCombat())// no wp movement in combat
            {
                Log.outError(LogFilter.Server, "SmartAI.StartPath: Creature entry {0} wanted to start waypoint movement while in combat, ignoring.", me.GetEntry());
                return;
            }
            if (HasEscortState(SmartEscortState.Escorting))
                StopPath();

            if (path != 0)
                if (!LoadPath(path))
                    return;

            if (mWayPoints == null || mWayPoints.Empty())
                return;

            AddEscortState(SmartEscortState.Escorting);
            mCanRepeatPath = repeat;

            SetRun(run);

            SmartPath wp = GetNextWayPoint();
            if (wp != null)
            {
                mLastOOCPos = me.GetPosition();
                me.GetMotionMaster().MovePoint(wp.id, wp.x, wp.y, wp.z);
                GetScript().ProcessEventsFor(SmartEvents.WaypointStart, null, wp.id, GetScript().GetPathId());
            }
        }

        bool LoadPath(uint entry)
        {
            if (HasEscortState(SmartEscortState.Escorting))
                return false;

            mWayPoints = Global.SmartAIMgr.GetPath(entry);
            if (mWayPoints == null)
            {
                GetScript().SetPathId(0);
                return false;
            }
            GetScript().SetPathId(entry);
            return true;
        }

        public void PausePath(uint delay, bool forced)
        {
            if (!HasEscortState(SmartEscortState.Escorting))
                return;
            if (HasEscortState(SmartEscortState.Paused))
            {
                Log.outError(LogFilter.Server, "SmartAI.PausePath: Creature entry {0} wanted to pause waypoint movement while already paused, ignoring.", me.GetEntry());
                return;
            }
            mForcedPaused = forced;
            mLastOOCPos = me.GetPosition();
            AddEscortState(SmartEscortState.Paused);
            mWPPauseTimer = delay;
            if (forced)
            {
                SetRun(mRun);
                me.StopMoving();//force stop
                me.GetMotionMaster().MoveIdle();//force stop
            }
            GetScript().ProcessEventsFor(SmartEvents.WaypointPaused, null, mLastWP.id, GetScript().GetPathId());
        }

        public void StopPath(uint DespawnTime = 0, uint quest = 0, bool fail = false)
        {
            if (!HasEscortState(SmartEscortState.Escorting))
                return;

            if (quest != 0)
                mEscortQuestID = quest;
            SetDespawnTime(DespawnTime);
            mDespawnTime = DespawnTime;

            mLastOOCPos = me.GetPosition();
            me.StopMoving();//force stop
            me.GetMotionMaster().MoveIdle();
            GetScript().ProcessEventsFor(SmartEvents.WaypointStopped, null, mLastWP.id, GetScript().GetPathId());
            EndPath(fail);
        }

        public void EndPath(bool fail = false)
        {
            GetScript().ProcessEventsFor(SmartEvents.WaypointEnded, null, mLastWP.id, GetScript().GetPathId());

            RemoveEscortState(SmartEscortState.Escorting | SmartEscortState.Paused | SmartEscortState.Returning);
            mWayPoints = null;
            mCurrentWPID = 0;
            mWPPauseTimer = 0;
            mLastWP = null;

            if (mCanRepeatPath)
            {
                if (IsAIControlled())
                    StartPath(mRun, GetScript().GetPathId(), true);
            }
            else
                GetScript().SetPathId(0);

            List<WorldObject> targets = GetScript().GetTargetList(SharedConst.SmartEscortTargets);
            if (targets != null && mEscortQuestID != 0)
            {
                if (targets.Count == 1 && GetScript().IsPlayer(targets.First()))
                {
                    Player player = targets.First().ToPlayer();
                    if (!fail && player.IsAtGroupRewardDistance(me) && player.GetCorpse() == null)
                        player.GroupEventHappens(mEscortQuestID, me);

                    if (fail && player.GetQuestStatus(mEscortQuestID) == QuestStatus.Incomplete)
                        player.FailQuest(mEscortQuestID);

                    Group group = player.GetGroup();
                    if (group)
                    {
                        for (GroupReference groupRef = group.GetFirstMember(); groupRef != null; groupRef = groupRef.next())
                        {
                            Player groupGuy = groupRef.GetSource();

                            if (!fail && groupGuy.IsAtGroupRewardDistance(me) && !groupGuy.GetCorpse())
                                groupGuy.AreaExploredOrEventHappens(mEscortQuestID);
                            if (fail && groupGuy.GetQuestStatus(mEscortQuestID) == QuestStatus.Incomplete)
                                groupGuy.FailQuest(mEscortQuestID);
                        }
                    }
                }
                else
                {
                    foreach (var obj in targets)
                    {
                        if (GetScript().IsPlayer(obj))
                        {
                            Player player = obj.ToPlayer();
                            if (!fail && player.IsAtGroupRewardDistance(me) && player.GetCorpse() == null)
                                player.AreaExploredOrEventHappens(mEscortQuestID);
                            if (fail && player.GetQuestStatus(mEscortQuestID) == QuestStatus.Incomplete)
                                player.FailQuest(mEscortQuestID);
                        }
                    }
                }
            }
            if (mDespawnState == 1)
                StartDespawn();
        }

        public void ResumePath()
        {
            SetRun(mRun);
            if (mLastWP != null)
                me.GetMotionMaster().MovePoint(mLastWP.id, mLastWP.x, mLastWP.y, mLastWP.z);
        }

        void ReturnToLastOOCPos()
        {
            if (!IsAIControlled())
                return;

            SetRun(mRun);
            me.GetMotionMaster().MovePoint(EventId.SmartEscortLastOCCPoint, mLastOOCPos);
        }

        void UpdatePath(uint diff)
        {
            if (!HasEscortState(SmartEscortState.Escorting))
                return;
            if (mEscortInvokerCheckTimer < diff)
            {
                if (!IsEscortInvokerInRange())
                {
                    StopPath(mDespawnTime, mEscortQuestID, true);
                }
                mEscortInvokerCheckTimer = 1000;
            }
            else mEscortInvokerCheckTimer -= diff;
            // handle pause
            if (HasEscortState(SmartEscortState.Paused))
            {
                if (mWPPauseTimer < diff)
                {
                    if (!me.IsInCombat() && !HasEscortState(SmartEscortState.Returning) && (mWPReached || mLastWPIDReached == EventId.SmartEscortLastOCCPoint || mForcedPaused))
                    {
                        GetScript().ProcessEventsFor(SmartEvents.WaypointResumed, null, mLastWP.id, GetScript().GetPathId());
                        RemoveEscortState(SmartEscortState.Paused);
                        if (mForcedPaused)// if paused between 2 wps resend movement
                        {
                            ResumePath();
                            mWPReached = false;
                            mForcedPaused = false;
                        }
                        if (mLastWPIDReached == EventId.SmartEscortLastOCCPoint)
                            mWPReached = true;
                    }
                    mWPPauseTimer = 0;
                }
                else
                {
                    mWPPauseTimer -= diff;
                }
            }
            if (HasEscortState(SmartEscortState.Returning))
            {
                if (mWPReached)//reached OOC WP
                {
                    RemoveEscortState(SmartEscortState.Returning);
                    if (!HasEscortState(SmartEscortState.Paused))
                        ResumePath();
                    mWPReached = false;
                }
            }
            if (me.IsInCombat() || HasEscortState(SmartEscortState.Paused | SmartEscortState.Returning))
                return;
            // handle next wp
            if (mWPReached)//reached WP
            {
                mWPReached = false;
                SmartPath wp = GetNextWayPoint();
                if (mCurrentWPID == GetWPCount())
                {
                    EndPath();
                }
                else if (wp != null)
                {
                    SetRun(mRun);
                    me.GetMotionMaster().MovePoint(wp.id, wp.x, wp.y, wp.z);
                }
            }
        }

        public override void UpdateAI(uint diff)
        {
            GetScript().OnUpdate(diff);
            UpdatePath(diff);
            UpdateDespawn(diff);

            UpdateFollow(diff);

            if (!IsAIControlled())
                return;

            if (!UpdateVictim())
                return;

            if (mCanAutoAttack)
                DoMeleeAttackIfReady();
        }

        bool IsEscortInvokerInRange()
        {
            var targets = GetScript().GetTargetList(SharedConst.SmartEscortTargets);
            if (targets != null)
            {
                if (targets.Count == 1 && GetScript().IsPlayer(targets.First()))
                {
                    Player player = targets.First().ToPlayer();
                    if (me.GetDistance(player) <= SMART_ESCORT_MAX_PLAYER_DIST)
                        return true;

                    Group group = player.GetGroup();
                    if (group)
                    {
                        for (GroupReference groupRef = group.GetFirstMember(); groupRef != null; groupRef = groupRef.next())
                        {
                            Player groupGuy = groupRef.GetSource();

                            if (me.GetDistance(groupGuy) <= SMART_ESCORT_MAX_PLAYER_DIST)
                                return true;
                        }
                    }
                }
                else
                {
                    foreach (var obj in targets)
                    {
                        if (GetScript().IsPlayer(obj))
                        {
                            if (me.GetDistance(obj.ToPlayer()) <= SMART_ESCORT_MAX_PLAYER_DIST)
                                return true;
                        }
                    }
                }
            }
            return true;//escort targets were not set, ignore range check
        }

        void MovepointReached(uint id)
        {
            if (id != EventId.SmartEscortLastOCCPoint && mLastWPIDReached != id)
                GetScript().ProcessEventsFor(SmartEvents.WaypointReached, null, id);

            mLastWPIDReached = id;
            mWPReached = true;
        }

        public override void MovementInform(MovementGeneratorType MovementType, uint Data)
        {
            if ((MovementType == MovementGeneratorType.Point && Data == EventId.SmartEscortLastOCCPoint) || MovementType == MovementGeneratorType.Follow)
                me.ClearUnitState(UnitState.Evade);

            GetScript().ProcessEventsFor(SmartEvents.Movementinform, null, (uint)MovementType, Data);
            if (MovementType != MovementGeneratorType.Point || !HasEscortState(SmartEscortState.Escorting))
                return;
            MovepointReached(Data);
        }

        void RemoveAuras()
        {
            //fixme: duplicated logic in CreatureAI._EnterEvadeMode (could use RemoveAllAurasExceptType)
            foreach (var pair in me.GetAppliedAuras())
            {
                Aura aura = pair.Value.GetBase();
                if (!aura.IsPassive() && !aura.HasEffectType(AuraType.ControlVehicle) && !aura.HasEffectType(AuraType.CloneCaster) && aura.GetCasterGUID() != me.GetGUID())
                    me.RemoveAura(pair);
            }
        }

        public override void EnterEvadeMode(EvadeReason why = EvadeReason.Other)
        {
            if (mEvadeDisabled)
            {
                GetScript().ProcessEventsFor(SmartEvents.Evade);
                return;
            }

            if (!_EnterEvadeMode())
                return;

            me.AddUnitState(UnitState.Evade);

            GetScript().ProcessEventsFor(SmartEvents.Evade);//must be after aura clear so we can cast spells from db

            SetRun(mRun);
            Unit target = !mFollowGuid.IsEmpty() ? Global.ObjAccessor.GetUnit(me, mFollowGuid) : null;
            Unit owner = me.GetCharmerOrOwner();
            if (HasEscortState(SmartEscortState.Escorting))
            {
                AddEscortState(SmartEscortState.Returning);
                ReturnToLastOOCPos();
            }
            else if (target)
            {
                me.GetMotionMaster().MoveFollow(target, mFollowDist, mFollowAngle);
                // evade is not cleared in MoveFollow, so we can't keep it
                me.ClearUnitState(UnitState.Evade);
            }
            else if (owner)
            {
                me.GetMotionMaster().MoveFollow(owner, SharedConst.PetFollowDist, SharedConst.PetFollowAngle);
                me.ClearUnitState(UnitState.Evade);
            }
            else
                me.GetMotionMaster().MoveTargetedHome();

            Reset();
        }

        public override void MoveInLineOfSight(Unit who)
        {
            if (who == null)
                return;

            GetScript().OnMoveInLineOfSight(who);

            if (!IsAIControlled())
                return;

            if (AssistPlayerInCombatAgainst(who))
                return;

            base.MoveInLineOfSight(who);
        }

        public override bool CanAIAttack(Unit victim)
        {
            return !me.HasReactState(ReactStates.Passive);
        }

        bool AssistPlayerInCombatAgainst(Unit who)
        {
            if (me.HasReactState(ReactStates.Passive) || !IsAIControlled())
                return false;

            if (who == null || who.GetVictim() == null)
                return false;

            //experimental (unknown) flag not present
            if (!me.GetCreatureTemplate().TypeFlags.HasAnyFlag(CreatureTypeFlags.CanAssist))
                return false;

            //not a player
            if (who.GetVictim().GetCharmerOrOwnerPlayerOrPlayerItself() == null)
                return false;

            //never attack friendly
            if (me.IsFriendlyTo(who))
                return false;

            //too far away and no free sight?
            if (me.IsWithinDistInMap(who, SMART_MAX_AID_DIST) && me.IsWithinLOSInMap(who))
            {
                //already fighting someone?
                if (me.GetVictim() == null)
                {
                    AttackStart(who);
                    return true;
                }
                else
                {
                    who.SetInCombatWith(me);
                    me.AddThreat(who, 0.0f);
                    return true;
                }
            }

            return false;
        }

        public override void JustRespawned()
        {
            mDespawnTime = 0;
            mRespawnTime = 0;
            mDespawnState = 0;
            mEscortState = SmartEscortState.None;
            me.SetVisible(true);
            if (me.getFaction() != me.GetCreatureTemplate().Faction)
                me.RestoreFaction();
            mJustReset = true;
            JustReachedHome();
            GetScript().ProcessEventsFor(SmartEvents.Respawn);
            mFollowGuid.Clear();//do not reset follower on Reset(), we need it after combat evade
            mFollowDist = 0;
            mFollowAngle = 0;
            mFollowCredit = 0;
            mFollowArrivedTimer = 1000;
            mFollowArrivedEntry = 0;
            mFollowCreditType = 0;
        }

        public override void JustReachedHome()
        {
            GetScript().OnReset();
            if (!mJustReset)
            {
                GetScript().ProcessEventsFor(SmartEvents.ReachedHome);

                if (!UpdateVictim() && me.GetMotionMaster().GetCurrentMovementGeneratorType() == MovementGeneratorType.Idle && me.GetWaypointPath() != 0)
                    me.GetMotionMaster().MovePath(me.GetWaypointPath(), true);
            }
            mJustReset = false;
        }

        public override void EnterCombat(Unit victim)
        {
            if (IsAIControlled())
                me.InterruptNonMeleeSpells(false); // must be before ProcessEvents

            GetScript().ProcessEventsFor(SmartEvents.Aggro, victim);

            if (!IsAIControlled())
                return;

            mLastOOCPos = me.GetPosition();
            SetRun(mRun);
            if (me.GetMotionMaster().GetMotionSlotType(MovementSlot.Active) == MovementGeneratorType.Point)
                me.GetMotionMaster().MovementExpired();
        }

        public override void JustDied(Unit killer)
        {
            GetScript().ProcessEventsFor(SmartEvents.Death, killer);
            if (HasEscortState(SmartEscortState.Escorting))
            {
                EndPath(true);
                me.StopMoving();//force stop
                me.GetMotionMaster().MoveIdle();
            }
        }

        public override void KilledUnit(Unit victim)
        {
            GetScript().ProcessEventsFor(SmartEvents.Kill, victim);
        }

        public override void JustSummoned(Creature summon)
        {
            GetScript().ProcessEventsFor(SmartEvents.SummonedUnit, summon);
        }

        public override void AttackStart(Unit who)
        {
            if (who != null && me.Attack(who, true))
            {
                SetRun(mRun);
                if (me.GetMotionMaster().GetCurrentMovementGeneratorType() == MovementGeneratorType.Point)
                    me.GetMotionMaster().MovementExpired();

                if (mCanCombatMove)
                    me.GetMotionMaster().MoveChase(who);

                mLastOOCPos = me.GetPosition();
            }
        }

        public override void SpellHit(Unit caster, SpellInfo spell)
        {
            GetScript().ProcessEventsFor(SmartEvents.SpellHit, caster, 0, 0, false, spell);
        }

        public override void SpellHitTarget(Unit target, SpellInfo spell)
        {
            GetScript().ProcessEventsFor(SmartEvents.SpellhitTarget, target, 0, 0, false, spell);
        }

        public override void DamageTaken(Unit attacker, ref uint damage)
        {
            GetScript().ProcessEventsFor(SmartEvents.Damaged, attacker, damage);

            if (!IsAIControlled()) // don't allow players to use unkillable units
                return;

            if (mInvincibilityHpLevel != 0 && (damage >= me.GetHealth() - mInvincibilityHpLevel))
                damage = (uint)(me.GetHealth() - mInvincibilityHpLevel);  // damage should not be nullified, because of player damage req.
        }

        public override void HealReceived(Unit by, uint addhealth)
        {
            GetScript().ProcessEventsFor(SmartEvents.ReceiveHeal, by, addhealth);
        }

        public override void ReceiveEmote(Player player, TextEmotes emoteId)
        {
            GetScript().ProcessEventsFor(SmartEvents.ReceiveEmote, player, (uint)emoteId);
        }

        public override void IsSummonedBy(Unit summoner)
        {
            GetScript().ProcessEventsFor(SmartEvents.JustSummoned, summoner);
        }

        public override void DamageDealt(Unit victim, ref uint damage, DamageEffectType damageType)
        {
            GetScript().ProcessEventsFor(SmartEvents.DamagedTarget, victim, damage);
        }

        public override void SummonedCreatureDespawn(Creature summon)
        {
            GetScript().ProcessEventsFor(SmartEvents.SummonDespawned, summon);
        }

        public override void CorpseRemoved(long respawnDelay)
        {
            GetScript().ProcessEventsFor(SmartEvents.CorpseRemoved, null, (uint)respawnDelay);
        }

        public override void PassengerBoarded(Unit passenger, sbyte seatId, bool apply)
        {
            GetScript().ProcessEventsFor(apply ? SmartEvents.PassengerBoarded : SmartEvents.PassengerRemoved, passenger, (uint)seatId, 0, apply);
        }

        public override void InitializeAI()
        {
            mScript.OnInitialize(me);
            if (!me.IsDead())
                mJustReset = true;
            JustReachedHome();
            GetScript().ProcessEventsFor(SmartEvents.Respawn);
        }

        public override void OnCharmed(bool apply)
        {
            if (apply) // do this before we change charmed state, as charmed state might prevent these things from processing
            {
                if (HasEscortState(SmartEscortState.Escorting | SmartEscortState.Paused | SmartEscortState.Returning))
                    EndPath(true);
                me.StopMoving();
            }
            mIsCharmed = apply;

            if (!apply && !me.IsInEvadeMode())
            {
                if (mCanRepeatPath)
                    StartPath(mRun, GetScript().GetPathId(), true);
                else
                    me.SetWalk(!mRun);

                Unit charmer = me.GetCharmer();
                if (charmer)
                    AttackStart(charmer);
            }

            GetScript().ProcessEventsFor(SmartEvents.Charmed, null, 0, 0, apply);
        }

        public override void DoAction(int param)
        {
            GetScript().ProcessEventsFor(SmartEvents.ActionDone, null, (uint)param);
        }

        public override uint GetData(uint id)
        {
            return 0;
        }

        public override void SetData(uint id, uint value)
        {
            GetScript().ProcessEventsFor(SmartEvents.DataSet, null, id, value);
        }

        public override void SetGUID(ObjectGuid guid, int id) { }

        public override ObjectGuid GetGUID(int id)
        {
            return ObjectGuid.Empty;
        }

        public void SetRun(bool run)
        {
            me.SetWalk(!run);
            mRun = run;
        }

        public void SetFly(bool fly)
        {
            me.SetDisableGravity(fly);
        }

        public void SetSwim(bool swim)
        {
            me.SetSwim(swim);
        }

        public void SetEvadeDisabled(bool disable)
        {
            mEvadeDisabled = disable;
        }

        public override void sGossipHello(Player player)
        {
            GetScript().ProcessEventsFor(SmartEvents.GossipHello, player);
        }

        public override void sGossipSelect(Player player, uint menuId, uint gossipListId)
        {
            GetScript().ProcessEventsFor(SmartEvents.GossipSelect, player, menuId, gossipListId);
        }

        public override void sGossipSelectCode(Player player, uint menuId, uint gossipListId, string code) { }

        public override void sQuestAccept(Player player, Quest quest)
        {
            GetScript().ProcessEventsFor(SmartEvents.AcceptedQuest, player, quest.Id);
        }

        public override void sQuestReward(Player player, Quest quest, uint opt)
        {
            GetScript().ProcessEventsFor(SmartEvents.RewardQuest, player, quest.Id, opt);
        }

        public override bool sOnDummyEffect(Unit caster, uint spellId, int effIndex)
        {
            GetScript().ProcessEventsFor(SmartEvents.DummyEffect, caster, spellId, (uint)effIndex);
            return true;
        }

        public void SetCombatMove(bool on)
        {
            if (mCanCombatMove == on)
                return;

            mCanCombatMove = on;
            if (!IsAIControlled())
                return;

            if (!HasEscortState(SmartEscortState.Escorting))
            {
                if (on && me.GetVictim() != null)
                {
                    if (me.GetMotionMaster().GetCurrentMovementGeneratorType() == MovementGeneratorType.Idle)
                    {
                        SetRun(mRun);
                        me.GetMotionMaster().MoveChase(me.GetVictim());
                        me.CastStop();
                    }
                }
                else
                {
                    if (me.HasUnitState(UnitState.ConfusedMove | UnitState.FleeingMove))
                        return;

                    me.GetMotionMaster().MovementExpired();
                    me.GetMotionMaster().Clear(true);
                    me.StopMoving();
                    me.GetMotionMaster().MoveIdle();
                }
            }
        }

        public void SetFollow(Unit target, float dist, float angle, uint credit, uint end, uint creditType)
        {
            if (target == null)
                return;

            mFollowGuid = target.GetGUID();
            mFollowDist = dist >= 0.0f ? dist : SharedConst.PetFollowDist;
            mFollowAngle = angle >= 0.0f ? angle : me.GetFollowAngle();
            mFollowArrivedTimer = 1000;
            mFollowCredit = credit;
            mFollowArrivedEntry = end;
            mFollowCreditType = creditType;
            SetRun(mRun);
            me.GetMotionMaster().MoveFollow(target, mFollowDist, mFollowAngle);
        }

        public void SetScript9(SmartScriptHolder e, uint entry, Unit invoker)
        {
            if (invoker != null)
                GetScript().mLastInvoker = invoker.GetGUID();
            GetScript().SetScript9(e, entry);
        }

        public override void sOnGameEvent(bool start, ushort eventId)
        {
            GetScript().ProcessEventsFor(start ? SmartEvents.GameEventStart : SmartEvents.GameEventEnd, null, eventId);
        }

        public override void OnSpellClick(Unit clicker, ref bool result)
        {
            if (!result)
                return;

            GetScript().ProcessEventsFor(SmartEvents.OnSpellclick, clicker);
        }

        public void UpdateFollow(uint diff)
        {
            if (!mFollowGuid.IsEmpty())
            {
                if (mFollowArrivedTimer < diff)
                {
                    if (me.FindNearestCreature(mFollowArrivedEntry, SharedConst.InteractionDistance, true) != null)
                    {
                        Player player = Global.ObjAccessor.GetPlayer(me, mFollowGuid);
                        if (player != null)
                        {
                            if (mFollowCreditType == 0)
                                player.RewardPlayerAndGroupAtEvent(mFollowCredit, me);
                            else
                                player.GroupEventHappens(mFollowCredit, me);
                        }
                        mFollowGuid.Clear();
                        mFollowDist = 0;
                        mFollowAngle = 0;
                        mFollowCredit = 0;
                        mFollowArrivedTimer = 1000;
                        mFollowArrivedEntry = 0;
                        mFollowCreditType = 0;
                        SetDespawnTime(5000);
                        me.StopMoving();
                        me.GetMotionMaster().MoveIdle();
                        StartDespawn();
                        GetScript().ProcessEventsFor(SmartEvents.FollowCompleted);
                        return;
                    }
                    mFollowArrivedTimer = 1000;
                }
                else mFollowArrivedTimer -= diff;
            }
        }

        bool HasEscortState(SmartEscortState uiEscortState) { return mEscortState.HasAnyFlag(uiEscortState); }
        void AddEscortState(SmartEscortState uiEscortState) { mEscortState |= uiEscortState; }
        void RemoveEscortState(SmartEscortState uiEscortState) { mEscortState &= ~uiEscortState; }
        public void SetAutoAttack(bool on) { mCanAutoAttack = on; }
        public bool CanCombatMove() { return mCanCombatMove; }

        public SmartScript GetScript() { return mScript; }

        public void SetInvincibilityHpLevel(uint level) { mInvincibilityHpLevel = level; }

        public void SetDespawnTime(uint t, uint r = 0)
        {
            mDespawnTime = t;
            mRespawnTime = r;
            mDespawnState = (uint)(t != 0 ? 1 : 0);
        }

        public void StartDespawn() { mDespawnState = 2; }

        int GetWPCount() { return mWayPoints != null ? mWayPoints.Count : 0; }

        bool mIsCharmed;
        uint mFollowCreditType;
        uint mFollowArrivedTimer;
        uint mFollowCredit;
        uint mFollowArrivedEntry;
        ObjectGuid mFollowGuid;
        float mFollowDist;
        float mFollowAngle;

        SmartScript mScript = new SmartScript();
        Dictionary<uint, SmartPath> mWayPoints;
        SmartEscortState mEscortState;
        uint mCurrentWPID;
        uint mLastWPIDReached;
        bool mWPReached;
        uint mWPPauseTimer;
        SmartPath mLastWP;
        Position mLastOOCPos;//set on enter combat

        bool mCanRepeatPath;
        bool mRun;
        bool mEvadeDisabled;
        bool mCanAutoAttack;
        bool mCanCombatMove;
        bool mForcedPaused;
        uint mInvincibilityHpLevel;

        uint mDespawnTime;
        uint mRespawnTime;
        uint mDespawnState;

        public uint mEscortQuestID;

        uint mEscortInvokerCheckTimer;
        bool mJustReset;
    }

    public class SmartGameObjectAI : GameObjectAI
    {
        public SmartGameObjectAI(GameObject g) : base(g)
        {
            mScript = new SmartScript();
        }

        public override void UpdateAI(uint diff)
        {
            GetScript().OnUpdate(diff);
        }

        public override void InitializeAI()
        {
            GetScript().OnInitialize(go);
            GetScript().ProcessEventsFor(SmartEvents.Respawn);
        }

        public override void Reset()
        {
            GetScript().OnReset();
        }

        public override bool GossipHello(Player player, bool isUse)
        {
            Log.outDebug(LogFilter.ScriptsAi, "SmartGameObjectAI.GossipHello");
            GetScript().ProcessEventsFor(SmartEvents.GossipHello, player, 0, 0, false, null, go);
            return false;
        }

        public override bool GossipSelect(Player player, uint sender, uint action)
        {
            GetScript().ProcessEventsFor(SmartEvents.GossipSelect, player, sender, action, false, null, go);
            return false;
        }

        public override bool GossipSelectCode(Player player, uint sender, uint action, string code)
        {
            return false;
        }

        public override bool QuestAccept(Player player, Quest quest)
        {
            GetScript().ProcessEventsFor(SmartEvents.AcceptedQuest, player, quest.Id, 0, false, null, go);
            return false;
        }

        public override bool QuestReward(Player player, Quest quest, uint opt)
        {
            GetScript().ProcessEventsFor(SmartEvents.RewardQuest, player, quest.Id, opt, false, null, go);
            return false;
        }

        public override uint GetDialogStatus(Player player)
        {
            return 100;
        }

        public override void Destroyed(Player player, uint eventId)
        {
            GetScript().ProcessEventsFor(SmartEvents.Death, player, eventId, 0, false, null, go);
        }

        public override void SetData(uint id, uint value)
        {
            GetScript().ProcessEventsFor(SmartEvents.DataSet, null, id, value);
        }

        public void SetScript9(SmartScriptHolder e, uint entry, Unit invoker)
        {
            if (invoker != null)
                GetScript().mLastInvoker = invoker.GetGUID();
            GetScript().SetScript9(e, entry);
        }

        public override void OnGameEvent(bool start, ushort eventId)
        {
            GetScript().ProcessEventsFor(start ? SmartEvents.GameEventStart : SmartEvents.GameEventEnd, null, eventId);
        }

        public override void OnStateChanged(uint state, Unit unit)
        {
            GetScript().ProcessEventsFor(SmartEvents.GoStateChanged, unit, state);
        }

        public override void EventInform(uint eventId)
        {
            GetScript().ProcessEventsFor(SmartEvents.GoEventInform, null, eventId);
        }

        public SmartScript GetScript() { return mScript; }

        SmartScript mScript;
    }

    public class SmartSpell : SpellScript
    {
        public override bool Load()
        {
            mScript.OnInitialize(GetSpell());
            scriptHolders = Global.SmartAIMgr.GetScript((int)GetSpellInfo().Id, SmartScriptType.Spell);
            return true;
        }

        void HandleEffectHit(uint effIndex)
        {
            mScript.ProcessEventsFor(SmartEvents.SpellEffectHit, GetCaster());
        }

        void HandleEffectHitTarget(uint effIndex)
        {
            mScript.ProcessEventsFor(SmartEvents.SpellEffectHitTarget);
        }

        public override void Register()
        {
            foreach (var holder in scriptHolders)
            {
                switch (holder.GetEventType())
                {
                    case SmartEvents.SpellEffectHit:
                        OnEffectHit.Add(new EffectHandler(HandleEffectHit, holder.Event.spell.effIndex, SpellEffectName.ScriptEffect));
                        OnEffectHit.Add(new EffectHandler(HandleEffectHit, holder.Event.spell.effIndex, SpellEffectName.Dummy));
                        break;
                    case SmartEvents.SpellEffectHitTarget:
                        OnEffectHitTarget.Add(new EffectHandler(HandleEffectHitTarget, holder.Event.spell.effIndex, SpellEffectName.ScriptEffect));
                        OnEffectHitTarget.Add(new EffectHandler(HandleEffectHitTarget, holder.Event.spell.effIndex, SpellEffectName.Dummy));
                        break;
                }

            }
        }

        List<SmartScriptHolder> scriptHolders = new List<SmartScriptHolder>();
        SmartScript mScript = new SmartScript();
    }

    [Script]
    class SmartTrigger : AreaTriggerScript
    {
        public SmartTrigger() : base("SmartTrigger") { }

        public override bool OnTrigger(Player player, AreaTriggerRecord trigger, bool entered)
        {
            if (!player.IsAlive())
                return false;

            Log.outDebug(LogFilter.ScriptsAi, "AreaTrigger {0} is using SmartTrigger script", trigger.Id);
            SmartScript script = new SmartScript();
            script.OnInitialize(trigger);
            script.ProcessEventsFor(SmartEvents.AreatriggerOntrigger, player, trigger.Id);
            return true;
        }
    }

    [Script]
    class SmartScene : SceneScript
    {
        public SmartScene() : base("SmartScene") { }

        public override void OnSceneStart(Player player, uint sceneInstanceID, SceneTemplate sceneTemplate)
        {
            SmartScript smartScript = new SmartScript();
            smartScript.OnInitialize(sceneTemplate);
            smartScript.ProcessEventsFor(SmartEvents.SceneStart, player);
        }

        public override void OnSceneTriggerEvent(Player player, uint sceneInstanceID, SceneTemplate sceneTemplate, string triggerName)
        {
            SmartScript smartScript = new SmartScript();
            smartScript.OnInitialize(sceneTemplate);
            smartScript.ProcessEventsFor(SmartEvents.SceneTrigger, player, 0, 0, false, null, null, triggerName);
        }

        public override void OnSceneCancel(Player player, uint sceneInstanceID, SceneTemplate sceneTemplate)
        {
            SmartScript smartScript = new SmartScript();
            smartScript.OnInitialize(sceneTemplate);
            smartScript.ProcessEventsFor(SmartEvents.SceneCancel, player);
        }

        public override void OnSceneComplete(Player player, uint sceneInstanceID, SceneTemplate sceneTemplate)
        {
            SmartScript smartScript = new SmartScript();
            smartScript.OnInitialize(sceneTemplate);
            smartScript.ProcessEventsFor(SmartEvents.SceneComplete, player);
        }
    }

    public enum SmartEscortState
    {
        None = 0x00,                        //nothing in progress
        Escorting = 0x01,                        //escort is in progress
        Returning = 0x02,                        //escort is returning after being in combat
        Paused = 0x04                         //will not proceed with waypoints before state is removed
    }
}
