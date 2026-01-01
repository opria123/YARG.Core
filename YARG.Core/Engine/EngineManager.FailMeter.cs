using YARG.Core.Chart;
using System;
using YARG.Core.Engine.Drums;
using YARG.Core.Engine.Guitar;
using YARG.Core.Engine.Keys;
using YARG.Core.Engine.Vocals;
using YARG.Core.Logging;

namespace YARG.Core.Engine
{
    public partial class EngineManager
    {
        private const float HAPPINESS_FAIL_THRESHOLD        = 0.0f;

        /// <summary>
        /// The amount of happiness lost if a single note/gem for non-vocal players is hit.
        /// </summary>
        private const float HAPPINESS_PER_NOTE_HIT          = HAPPINESS_PER_NOTE_MISS / 4;

        /// <summary>
        /// The amount of happiness lost if a single note/gem for non-vocal players is completely missed.
        /// Note that this value also controls the amount of happiness lost for overstrums/overhits, but this is further scaled by the
        /// <see cref="YARG.Core.Game.RockMeterPreset.OverHitDamageMultiplier">OverHitDamageMultiplier</see> value of the current RockMeterPreset.
        /// </summary>
        private const float HAPPINESS_PER_NOTE_MISS         = 1.0f / 42;

        /// <summary>
        /// The amount of happiness gained if a vocal phrase is completed with an AWESOME rating.
        /// The exact amount gained scales depending on how far above the <see cref="VOCAL_HIT_PERC_MIDPOINT">VOCAL_HIT_PERC_MIDPOINT</see> value the player is.
        /// </summary>
        private const float HAPPINESS_PER_VOCAL_PHRASE_HIT  = 6.0f / 42;

        /// <summary>
        /// The amount of happiness lost if a vocal phrase is missed completely.
        /// The exact amount lost scales depending on how far below the <see cref="VOCAL_HIT_PERC_MIDPOINT">VOCAL_HIT_PERC_MIDPOINT</see> value the player is.
        /// </summary>
        private const float HAPPINESS_PER_VOCAL_PHRASE_MISS = 12.0f / 42;

        /// <summary>
        /// The hit percent at which no happiness is gained or lost for a vocal phrase.
        /// </summary>
        private const float VOCAL_HIT_PERC_MIDPOINT         = 0.75f;

        /// <summary>
        /// The amount of happiness required for a song's crowd stem to be enabled, if available.
        /// </summary>
        /// This is tuned to be slightly below the default starting happiness so songs with crowd cheering at
        /// the start will have the crowd stem enabled
        private const float HAPPINESS_CROWD_THRESHOLD       = 0.83f;

        /// <summary>
        /// The absolute minimum happiness value for a single player.
        /// </summary>
        private const float HAPPINESS_MINIMUM               = -3f;

        public  float Happiness => GetAverageHappiness();

        // We set this to max because the crowd stem is enabled by default and we want the first
        // update to disable the crowd stem when the rock meter preset has an initial happiness
        // below the crowd threshold
        private float _previousHappiness = 100f;

        private int   _starpowerCount = 0;

        public        bool IsAnyStarpowerActive => _starpowerCount > 0;

        public delegate void SongFailed();
        public delegate void HappinessOverThreshold();
        public delegate void HappinessUnderThreshold();
        public delegate void PlayerRevived(int engineId, float newHappiness);
        public delegate void PlayerFailed(int engineId);

        public event SongFailed? OnSongFailed;
        public event HappinessOverThreshold? OnHappinessOverThreshold;
        public event HappinessUnderThreshold? OnHappinessUnderThreshold;
        public event PlayerRevived? OnPlayerRevived;
        /// <summary>
        /// Fired when an individual player's happiness drops to the fail threshold.
        /// The player can still be revived via Star Power.
        /// </summary>
        public event PlayerFailed? OnPlayerFailed;

        /// <summary>
        /// The amount of happiness to give a revived player.
        /// Set to 50% to give them a fighting chance without making revival too easy.
        /// </summary>
        private const float REVIVAL_HAPPINESS = 0.5f;

        public void InitializeHappiness()
        {
            foreach (var container in _allEngines)
            {
                container.ResetHappiness();
            }

            UpdateHappiness();
        }

        /// <summary>
        /// Attempts to revive all failed players when Star Power is activated.
        /// Returns true if any player was revived.
        /// </summary>
        /// <returns>True if at least one player was revived.</returns>
        public bool TryReviveFailedPlayers()
        {
            return TryReviveFailedPlayers(null);
        }

        /// <summary>
        /// Attempts to revive failed players, optionally filtering by a predicate.
        /// </summary>
        /// <param name="shouldRevive">Optional predicate to filter which engine IDs should be revived. If null, all failed players are revived.</param>
        /// <returns>True if at least one player was revived.</returns>
        public bool TryReviveFailedPlayers(Func<int, bool>? shouldRevive)
        {
            bool anyRevived = false;
            
            foreach (var container in _allEngines)
            {
                // Check if this engine's happiness is at or below the fail threshold
                if (container.Happiness <= HAPPINESS_FAIL_THRESHOLD)
                {
                    // Apply filter if provided
                    if (shouldRevive != null && !shouldRevive(container.EngineId))
                    {
                        continue;
                    }
                    
                    container.RevivePlayer(REVIVAL_HAPPINESS);
                    OnPlayerRevived?.Invoke(container.EngineId, REVIVAL_HAPPINESS);
                    anyRevived = true;
                    
                    YargLogger.LogFormatInfo("[EngineManager] Revived player (EngineId: {0}) with {1:P0} happiness via Star Power", 
                        container.EngineId, REVIVAL_HAPPINESS);
                }
            }
            
            if (anyRevived)
            {
                UpdateHappiness();
            }
            
            return anyRevived;
        }

        /// <summary>
        /// Revives a specific player by engine ID.
        /// Used for network-synced revivals.
        /// </summary>
        /// <param name="engineId">The engine ID of the player to revive.</param>
        /// <param name="targetHappiness">The happiness level to set (default: 0.5).</param>
        /// <returns>True if the player was found and revived.</returns>
        public bool RevivePlayer(int engineId, float targetHappiness = 0.5f)
        {
            if (_allEnginesById.TryGetValue(engineId, out var container))
            {
                container.RevivePlayer(targetHappiness);
                OnPlayerRevived?.Invoke(engineId, targetHappiness);
                UpdateHappiness();
                
                YargLogger.LogFormatInfo("[EngineManager] Revived player (EngineId: {0}) with {1:P0} happiness", 
                    engineId, targetHappiness);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Checks if any player has failed (happiness at or below fail threshold).
        /// </summary>
        public bool HasAnyFailedPlayer()
        {
            foreach (var container in _allEngines)
            {
                if (container.Happiness <= HAPPINESS_FAIL_THRESHOLD)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Returns true if all players have failed.
        /// </summary>
        public bool HaveAllPlayersFailed()
        {
            if (_allEngines.Count == 0)
                return false;
                
            foreach (var container in _allEngines)
            {
                if (!container.HasFailed)
                {
                    return false;
                }
            }
            return true;
        }
        
        /// <summary>
        /// Gets the number of players who have failed.
        /// </summary>
        public int GetFailedPlayerCount()
        {
            int count = 0;
            foreach (var container in _allEngines)
            {
                if (container.HasFailed)
                {
                    count++;
                }
            }
            return count;
        }
        
        /// <summary>
        /// Gets the number of players who are still alive (not failed).
        /// </summary>
        public int GetAlivePlayerCount()
        {
            return _allEngines.Count - GetFailedPlayerCount();
        }

        private bool UpdateHappiness()
        {
            // Check if ALL players have failed - only then trigger OnSongFailed
            if (HaveAllPlayersFailed())
            {
                OnSongFailed?.Invoke();
                return true;
            }

            // Send over threshold event when happiness goes from below threshold to above
            if (Happiness >= HAPPINESS_CROWD_THRESHOLD && _previousHappiness < HAPPINESS_CROWD_THRESHOLD)
            {
                OnHappinessOverThreshold?.Invoke();
            }
            // Send under threshold event when happiness goes from above threshold to below
            else if (Happiness < HAPPINESS_CROWD_THRESHOLD && _previousHappiness >= HAPPINESS_CROWD_THRESHOLD)
            {
                OnHappinessUnderThreshold?.Invoke();
            }

            _previousHappiness = Happiness;

            return false;
        }

        private float GetLowestHappiniess()
        {
            float happiness = 1.0f;
            foreach (var engine in _allEngines)
            {
                if (engine.Happiness < happiness)
                {
                    happiness = engine.Happiness;
                }
            }
            return happiness;
        }

        private float GetAverageHappiness()
        {
            float happiness = 0.0f;
            foreach (var engine in _allEngines)
            {
                happiness += engine.Happiness;
            }

            return happiness / _allEngines.Count;
        }

        public partial class EngineContainer
        {
            public float Happiness { get; private set; } = 0.0f;
            
            /// <summary>
            /// Whether this player has failed (happiness at or below fail threshold).
            /// A failed player can be revived via Star Power.
            /// </summary>
            public bool HasFailed { get; private set; } = false;
            
            /// <summary>
            /// Grace period after revival (in seconds) where no happiness damage is taken.
            /// This gives the player time for their track to raise back up.
            /// Set to 4 seconds as a reasonable default (actual value may be tempo-based in GameManager).
            /// </summary>
            private const double REVIVAL_GRACE_PERIOD = 4.0;
            
            /// <summary>
            /// The engine time when the grace period ends. -1 = no grace period active.
            /// </summary>
            private double _graceEndTime = -1;
            
            /// <summary>
            /// Fallback: number of negative happiness events to ignore during grace period.
            /// Used when engine time isn't advancing (e.g., remote players).
            /// </summary>
            private int _graceEventsRemaining = 0;
            
            /// <summary>
            /// Maximum number of miss events to ignore during grace period.
            /// At typical note density, this is roughly equivalent to 4 seconds.
            /// </summary>
            private const int GRACE_EVENTS_MAX = 40;
            
            /// <summary>
            /// Set to true when this player has been revived locally (via Star Power from another player).
            /// Used to prevent duplicate revival events when network sync catches up.
            /// Reset when the player fails again.
            /// </summary>
            private bool _wasRevivedLocally = false;
            
            /// <summary>
            /// Whether the player is currently in a post-revival grace period.
            /// Uses time-based check if engine time is advancing, otherwise event-count fallback.
            /// </summary>
            public bool IsInGracePeriod 
            {
                get
                {
                    // Time-based grace period (for local players)
                    if (_graceEndTime > 0 && Engine.CurrentTime < _graceEndTime)
                    {
                        return true;
                    }
                    // Event-count fallback (for remote players whose engine time may not advance)
                    return _graceEventsRemaining > 0;
                }
            }

            public void ResetHappiness()
            {
                Happiness = RockMeterPreset.StartingHappiness;
                HasFailed = false;
                _wasRevivedLocally = false;
                _graceEndTime = -1;
                _graceEventsRemaining = 0;
            }

            /// <summary>
            /// Revives this player by setting their happiness to the specified level.
            /// Used when Star Power is activated to bring back failed players.
            /// Grants a grace period where no damage is taken to allow the track to raise.
            /// </summary>
            /// <param name="targetHappiness">The happiness level to set (0.0 to 1.0).</param>
            public void RevivePlayer(float targetHappiness)
            {
                Happiness = Math.Clamp(targetHappiness, 0.1f, RockMeterPreset.StartingHappiness);
                HasFailed = false;
                // Mark that we revived this player locally - this prevents duplicate
                // revival events when network sync catches up with our local state
                _wasRevivedLocally = true;
                // Grant grace period so player has time for track to come back
                // Time-based for local players
                _graceEndTime = Engine.CurrentTime + REVIVAL_GRACE_PERIOD;
                // Event-count fallback for remote players
                _graceEventsRemaining = GRACE_EVENTS_MAX;
            }

            private void OnVocalPhraseHit(double hitPercentAfterParams, bool fullPoints, bool isLastPhrase)
            {
                hitPercentAfterParams = Math.Clamp(hitPercentAfterParams, 0.0, 1.0);
                var delta = 0.0f;

                // If the hit percent is below the midpoint, the player loses happiness based on how far they are from the midpoint
                if (hitPercentAfterParams < VOCAL_HIT_PERC_MIDPOINT)
                {
                    delta = -1 * HAPPINESS_PER_VOCAL_PHRASE_MISS * RockMeterPreset.VocalsMissDamageMultiplier;
                    delta *= 1 - YargMath.InverseLerpF(0.0f, VOCAL_HIT_PERC_MIDPOINT, hitPercentAfterParams);
                }
                // If the hit percent is above the midpoint, the player gains happiness based on how far they are from the midpoint
                else
                {
                    delta = HAPPINESS_PER_VOCAL_PHRASE_HIT * RockMeterPreset.VocalsHitRecoveryMultiplier;
                    delta *= YargMath.InverseLerpF(VOCAL_HIT_PERC_MIDPOINT, 1.0f, hitPercentAfterParams);
                    if (_engineManager.IsAnyStarpowerActive)
                    {
                        delta *= RockMeterPreset.StarPowerEffectMultiplier;
                    }
                }

                AddHappiness(delta);
            }

            private void OnNoteHit<TNote>(int index, TNote note) where TNote : Note<TNote>
            {
                // Ignore any notes that have not been fully hit yet on the assumption that a call
                // where the note group was fully hit will eventually come if they are all hit
                if (!note.WasFullyHit())
                {
                    return;
                }

                var delta = HAPPINESS_PER_NOTE_HIT * RockMeterPreset.HitRecoveryMultiplier;
                if (_engineManager.IsAnyStarpowerActive)
                {
                    delta *= RockMeterPreset.StarPowerEffectMultiplier;
                }

                AddHappiness(delta);
            }

            private void OnNoteMissed<TNote>(int index, TNote note) where TNote : Note<TNote>
            {
                if (!note.WasFullyMissed())
                {
                    return;
                }

                var delta = -1 * HAPPINESS_PER_NOTE_MISS * RockMeterPreset.MissDamageMultiplier;
                AddHappiness(delta);
            }

            private void OnOverstrum()
            {
                var delta = -1 * HAPPINESS_PER_NOTE_MISS * RockMeterPreset.OverhitDamageMultiplier;
                AddHappiness(delta);
            }

            private void OnKeysOverhit(int key) => OnOverstrum();

            public void SyncRemoteStarPowerState(bool active)
            {
                OnStarPowerStatus(active);
            }

            /// <summary>
            /// Syncs the happiness and fail state from the authoritative source (the player's local client).
            /// Used for remote players in multiplayer to avoid happiness calculation desync.
            /// </summary>
            /// <param name="remoteHappiness">The happiness value from the authoritative client.</param>
            /// <param name="remoteFailed">Whether the player has failed according to the authoritative client.</param>
            public void SyncRemoteHappiness(float remoteHappiness, bool remoteFailed)
            {
                bool wasFailedBefore = HasFailed;
                
                // Directly set happiness from authoritative source
                Happiness = Math.Clamp(remoteHappiness, HAPPINESS_MINIMUM, 1f);
                
                // Validate fail state consistency:
                // If happiness is at or below fail threshold but remoteFailed is false,
                // this is inconsistent state (likely a race condition or desync).
                // Treat it as failed to prevent spurious revival events.
                if (Happiness <= HAPPINESS_FAIL_THRESHOLD && !remoteFailed)
                {
                    // Don't trust the remoteFailed=false if happiness indicates failure
                    // The player should be in failed state
                    HasFailed = true;
                }
                else
                {
                    HasFailed = remoteFailed;
                }
                
                // Detect state transitions
                if (!wasFailedBefore && HasFailed)
                {
                    // Player just failed - fire the event
                    // Also clear the local revival flag since they've failed again
                    _wasRevivedLocally = false;
                    _engineManager.OnPlayerFailed?.Invoke(EngineId);
                }
                else if (wasFailedBefore && !HasFailed)
                {
                    // Player was revived according to network state.
                    // Only fire the revival event if:
                    // 1. We didn't already process it locally (prevents duplicates from Star Power)
                    // 2. The happiness is at a reasonable revival level (at least 10%)
                    //    This prevents spurious revivals from desync where happiness is very low
                    //    but hasFailed flag hasn't caught up yet.
                    //    True revivals via Star Power set happiness to REVIVAL_HAPPINESS (50%).
                    const float MIN_REVIVAL_HAPPINESS = 0.1f;
                    
                    if (!_wasRevivedLocally && Happiness >= MIN_REVIVAL_HAPPINESS)
                    {
                        _graceEndTime = Engine.CurrentTime + REVIVAL_GRACE_PERIOD;
                        _graceEventsRemaining = GRACE_EVENTS_MAX;
                        _engineManager.OnPlayerRevived?.Invoke(EngineId, remoteHappiness);
                    }
                    else if (!_wasRevivedLocally && Happiness < MIN_REVIVAL_HAPPINESS)
                    {
                        // Low happiness but not failed - likely a desync.
                        // Don't fire revival event, but mark as failed to be safe.
                        HasFailed = true;
                    }
                    // Note: _wasRevivedLocally stays true until the player fails again
                }
                
                _engineManager.UpdateHappiness();
            }

            /// <summary>
            /// Applies a remote note result for tracking purposes.
            /// Note: This method no longer modifies happiness because for remote players,
            /// the authoritative happiness/fail state comes from SyncRemoteHappiness().
            /// Local happiness calculations would cause race conditions with network state
            /// (e.g., local engine detects fail while network says player is still alive,
            /// then network sync triggers a false "revival").
            /// </summary>
            public void ApplyRemoteNoteResult(int noteWeight, bool wasHit)
            {
                // NOTE: Intentionally does NOT call AddHappiness anymore.
                // For remote players, happiness and fail state are synced authoritatively
                // via SyncRemoteHappiness() from the network. If we calculated happiness
                // locally here, it would race with network state and cause issues like:
                // - Local engine detects fail (based on local note resolution timing)
                // - Network sync arrives with hasFailed=false (remote client's actual state)
                // - This triggers OnPlayerRevived (false revival)
                // - Then network sync with hasFailed=true arrives and triggers OnPlayerFailed again
                // 
                // By relying solely on SyncRemoteHappiness for happiness/fail state,
                // spectator tracks accurately reflect the remote player's actual state.
            }

            private void AddHappiness(float delta)
            {
                // Don't process happiness changes for already failed players
                if (HasFailed)
                {
                    return;
                }
                
                // During grace period after revival, ignore negative happiness changes
                // This gives the player time for their track to raise back up
                if (delta < 0 && IsInGracePeriod)
                {
                    // Decrement event counter for the fallback grace system
                    if (_graceEventsRemaining > 0)
                    {
                        _graceEventsRemaining--;
                    }
                    return;
                }
                
                Happiness = Math.Clamp(Happiness + delta, HAPPINESS_MINIMUM, 1f);

                // Check if this player just failed
                if (Happiness <= HAPPINESS_FAIL_THRESHOLD && !HasFailed)
                {
                    HasFailed = true;
                    _engineManager.OnPlayerFailed?.Invoke(EngineId);
                }

                _engineManager.UpdateHappiness();
            }

            private void SubscribeToEngineEvents()
            {
                // Subscribe to OnNoteHit and OnNoteMissed events
                if (Engine is BaseEngine<GuitarNote,GuitarEngineParameters,GuitarStats> guitarEngine)
                {
                    var engine = (GuitarEngine) guitarEngine;
                    engine.OnNoteHit += OnNoteHit;
                    engine.OnNoteMissed += OnNoteMissed;
                    engine.OnStarPowerStatus += OnStarPowerStatus;
                    engine.OnOverstrum += OnOverstrum;
                }

                if (Engine is BaseEngine<DrumNote, DrumsEngineParameters, DrumsStats> drumEngine)
                {
                    var engine = (DrumsEngine) drumEngine;
                    engine.OnNoteHit += OnNoteHit;
                    engine.OnNoteMissed += OnNoteMissed;
                    engine.OnStarPowerStatus += OnStarPowerStatus;
                    engine.OnOverhit += OnOverstrum;
                }

                if (Engine is BaseEngine<ProKeysNote, KeysEngineParameters, KeysStats>
                    proKeysEngine)
                {
                    var engine = (ProKeysEngine) proKeysEngine;
                    engine.OnNoteHit += OnNoteHit;
                    engine.OnNoteMissed += OnNoteMissed;
                    engine.OnStarPowerStatus += OnStarPowerStatus;
                    engine.OnOverhit += OnKeysOverhit;
                }

                if (Engine is BaseEngine<GuitarNote, KeysEngineParameters, KeysStats> keysEngine)
                {
                    var engine = (FiveLaneKeysEngine) keysEngine;
                    engine.OnNoteHit += OnNoteHit;
                    engine.OnNoteMissed += OnNoteMissed;
                    engine.OnStarPowerStatus += OnStarPowerStatus;
                    engine.OnOverhit += OnKeysOverhit;
                }

                if (Engine is VocalsEngine vocalsEngine)
                {
                    vocalsEngine.OnPhraseHit += OnVocalPhraseHit;
                    vocalsEngine.OnStarPowerStatus += OnStarPowerStatus;
                }
            }
        }
    }
}