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
    BlockSession? GetSessionById(int id);
    BlockSession StartSession(int profileId, long durationSeconds, bool locked);
    void EndActiveSession();
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
}
