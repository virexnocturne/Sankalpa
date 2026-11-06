namespace Blocker.Core;

public interface IBlockStore
{
    Profile? GetProfileByName(string name);
    Profile? GetProfileById(int id);
    List<Profile> ListProfiles();
    Profile SaveProfile(Profile profile);
    bool RenameProfile(int id, string newName);
    bool DeleteProfileByName(string name);
    bool DeleteProfile(int id);
    void DisableAllProfiles();

    BlockSession? GetActiveSession();
    List<BlockSession> GetActiveSessions()
    {
        var s = GetActiveSession();
        return s != null ? new List<BlockSession> { s } : new List<BlockSession>();
    }
    BlockSession? GetActiveSession(int? profileId)
    {
        if (!profileId.HasValue) return GetActiveSession();
        return GetActiveSessions().Find(s => s.ProfileId == profileId.Value);
    }
    BlockSession? GetSessionById(int id);
    BlockSession StartSession(int profileId, long durationSeconds, bool locked);
    void EndActiveSession();
    void EndActiveSession(int? profileId)
    {
        if (!profileId.HasValue)
        {
            EndActiveSession();
        }
        else
        {
            var s = GetActiveSession(profileId.Value);
            if (s != null) EndSession(s.Id);
        }
    }
    void EndSession(int sessionId);

    bool IsEmergencyUnlockPresent();
    void ConsumeEmergencyUnlock();
    bool CleanHosts();

    bool ServiceElevated { get; set; }
    bool HostsBlockingOk { get; set; }
    HostsState HostsState { get; set; }
    int HostsDomainCount { get; set; }
    string? HostsLastError { get; set; }
    List<string> GetCurrentHostsDomains();

    void RecordBlockEvent(BlockEvent evt);
    List<BlockEvent> GetBlockEvents(long sinceId);

    FallbackPlanner FallbackPlanner { get; }
    FallbackPlanResult? LastFallbackPlan { get; set; }

    DnsConfig GetDnsConfig();
    void SaveDnsConfig(DnsConfig config);

    MeditationSession? GetActiveMeditationSession() => null;
    MeditationSession StartMeditationSession(long targetDurationSeconds, List<string> allowedApps, LockMode lockMode, string? soundscape) => new();
    void EndMeditationSession(int id, bool completed, string? finalNotes = null) { }
    void UpdateMeditationNotes(int id, string notes) { }
    List<MeditationSession> GetMeditationHistory(int limit = 50) => new();
    List<MeditationSchedule> GetMeditationSchedules() => new();
    MeditationSchedule SaveMeditationSchedule(MeditationSchedule schedule) => schedule;
    bool DeleteMeditationSchedule(int id) => true;
    bool SnoozeMeditationSchedule(int id, int snoozeMinutes = 5) => true;
}
