﻿using Microsoft.EntityFrameworkCore.ChangeTracking;
using NexusForever.Shared.GameTable;
using NexusForever.WorldServer.Database.Character.Model;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Network.Message.Model;
using System;

namespace NexusForever.WorldServer.Game.Entity
{
    public class XpManager
    {
        private Player player;
        private PlayerSaveMask saveMask;

        public uint TotalXp
        {
            get => totalXp;
            set
            {
                totalXp = value;
                saveMask |= PlayerSaveMask.Xp;
            }
        }
        private uint totalXp;

        public uint RestBonusXp
        {
            get => restBonusXp;
            set
            {
                restBonusXp = value;
                saveMask |= PlayerSaveMask.Xp;
            }
        }
        private uint restBonusXp;

        public XpManager(Player player, Character model)
        {
            this.player = player;
            totalXp = model.TotalXp;

            CalculateRestXpAtLogin(model);
        }

        public void Save(CharacterContext context)
        {
            if (saveMask == PlayerSaveMask.None)
                return;

            if ((saveMask & PlayerSaveMask.Xp) != 0)
            {
                // character is attached in Player::Save, this will only be local lookup
                Character character = context.Character.Find(player.CharacterId);
                character.TotalXp = TotalXp;
                character.RestBonusXp = RestBonusXp;

                EntityEntry<Character> entity = context.Entry(character);
                entity.Property(p => p.TotalXp).IsModified = true;
                entity.Property(p => p.RestBonusXp).IsModified = true;
            }

            saveMask = PlayerSaveMask.None;
        }

        private void CalculateRestXpAtLogin(Character model)
        {
            if (model.LastOnline == null)
                return;

            uint maximumBonusXp;
            if (player.Level < 50)
                maximumBonusXp = (uint)((GameTableManager.Instance.XpPerLevel.GetEntry(player.Level + 1).MinXpForLevel - GameTableManager.Instance.XpPerLevel.GetEntry(player.Level).MinXpForLevel) * 1.5f);
            else
                maximumBonusXp = 0; // TODO: Calculate Elder Gem Rest Bonus XP

            double xpPercentEarned;

            // TODO: Calculate Rest Bonus XP earned since last login, properly.
            // We use DateTime.Now because LastOnline is stored by the DB in local time
            // Data from this video was used in initial calculations: https://www.youtube.com/watch?v=xEMMd7CGg4s
            // Video is out of date, but the assumption is the formulas are the same just modified more post-F2P.
            double hoursSinceLogin = DateTime.Now.Subtract((DateTime)model.LastOnline).TotalHours;
            switch (model.WorldId)
            {
                case 1229:
                    // TODO: Apply bonuses from decor or other things that increase rested XP gain.
                    xpPercentEarned = hoursSinceLogin * 0.0024f;
                    break;
                // TODO: Add support for home cities, towns and sleeping bag (?!) gain rates.
                default:
                    xpPercentEarned = 0d;
                    break;
            }

            // TODO: Apply bonuses from spells as necessary

            uint bonusXpValue = Math.Clamp((uint)((GameTableManager.Instance.XpPerLevel.GetEntry(player.Level + 1).MinXpForLevel - GameTableManager.Instance.XpPerLevel.GetEntry(player.Level).MinXpForLevel) * xpPercentEarned), 0, maximumBonusXp);
            uint totalBonusXp = Math.Clamp(model.RestBonusXp + bonusXpValue, 0u, maximumBonusXp);
            RestBonusXp = totalBonusXp;
        }

        /// Grants <see cref="Player"/> the supplied experience, handling level up if necessary.
        /// </summary>
        /// <param name="xp">Experience to grant</param>
        /// <param name="reason"><see cref="ExpReason"/> for the experience grant</param>
        public void GrantXp(uint earnedXp, ExpReason reason = ExpReason.Cheat)
        {
            const uint maxLevel = 50;

            if (earnedXp < 1)
                return;

            //if (!IsAlive)
            //    return;

            if (player.Level >= maxLevel)
                return;

            // TODO: Apply XP bonuses from current spells or active events

            // Signature XP rate was 25% extra. 
            uint signatureXp = 0u;
            if (player.SignatureEnabled)
                signatureXp = (uint)(earnedXp * 0.25f); // TODO: Make rate configurable.

            // Calculate Rest XP Bonus
            uint restXp = 0u;
            if (reason == ExpReason.KillCreature)
                restXp = (uint)(earnedXp * 0.5f);

            uint currentLevel = player.Level;
            uint currentXp = TotalXp;
            uint xpToNextLevel = GameTableManager.Instance.XpPerLevel.GetEntry(player.Level + 1).MinXpForLevel;
            uint totalXp = earnedXp + currentXp + signatureXp + restXp;

            player.Session.EnqueueMessageEncrypted(new ServerExperienceGained
            {
                TotalXpGained = earnedXp + signatureXp + restXp,
                RestXpAmount = restXp,
                SignatureXpAmount = signatureXp,
                Reason = reason
            });

            while (totalXp >= xpToNextLevel && currentLevel < maxLevel) // WorldServer.Rules.MaxLevel)
            {
                totalXp -= xpToNextLevel;

                if (currentLevel < maxLevel)
                    GrantLevel((byte)(player.Level + 1));
                else
                {
                    if (totalXp > 0)
                        earnedXp -= totalXp;
                }

                currentLevel = player.Level;

                xpToNextLevel = GameTableManager.Instance.XpPerLevel.GetEntry(player.Level + 1).MinXpForLevel;
            }

            SetXp(earnedXp + currentXp + signatureXp + restXp);
        }

        /// <summary>
        /// Sets <see cref="Player"/> <see cref="TotalXp"/> to supplied value
        /// </summary>
        /// <param name="xp"></param>
        private void SetXp(uint xp)
        {
            TotalXp = xp;
        }

        /// <summary>
        /// Sets <see cref="Player"/> to the supplied level and adjusts XP accordingly. Mainly for use with GM commands.
        /// </summary>
        /// <param name="newLevel">New level to be set</param>
        /// <param name="reason"><see cref="ExpReason"/> for the level grant</param>
        public void SetLevel(byte newLevel, ExpReason reason = ExpReason.Cheat)
        {
            uint oldLevel = player.Level;

            if (newLevel == oldLevel)
                return;

            uint newXp = GameTableManager.Instance.XpPerLevel.GetEntry(newLevel).MinXpForLevel;
            player.Session.EnqueueMessageEncrypted(new ServerExperienceGained
            {
                TotalXpGained = newXp - TotalXp,
                RestXpAmount = 0,
                SignatureXpAmount = 0,
                Reason = reason
            });
            SetXp(newXp);

            GrantLevel(newLevel);
        }

        /// <summary>
        /// Grants <see cref="Player"/> the supplied level and adjusts XP accordingly
        /// </summary>
        /// <param name="newLevel">New level to be set</param>
        private void GrantLevel(byte newLevel)
        {
            uint oldLevel = player.Level;

            if (newLevel == oldLevel)
                return;

            player.Level = newLevel;

            // Grant Rewards for level up
            player.SpellManager.GrantSpells();
            // Unlock LAS slots
            // Unlock AMPs
            // Add feature access
        }
    }
}