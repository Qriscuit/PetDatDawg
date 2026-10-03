namespace PetDaDog.Unity
{
    /// <summary>
    /// Values displayed by the native status window. Pets is null until the
    /// backend has returned a Steam-Inventory-confirmed value.
    /// </summary>
    public readonly struct StatusSnapshot
    {
        public StatusSnapshot(long? confirmedPets, int pendingGrantCount, string backendStatus, string steamStatus)
        {
            ConfirmedPets = confirmedPets;
            PendingGrantCount = pendingGrantCount;
            BackendStatus = backendStatus ?? string.Empty;
            SteamStatus = steamStatus ?? string.Empty;
        }

        public long? ConfirmedPets { get; }
        public int PendingGrantCount { get; }
        public string BackendStatus { get; }
        public string SteamStatus { get; }
    }

    public enum DogMouseButton
    {
        Left,
        Right,
    }
}
