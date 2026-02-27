using System.Reflection;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace DuelCs2;

public sealed class DuelPlugin : BasePlugin
{
    public override string ModuleName => "Duel CS2";
    public override string ModuleVersion => "1.4.0";
    public override string ModuleAuthor => "Codex";
    public override string ModuleDescription => "Duels dynamiques globaux/personnalisés sur zones configurées.";

    private const float DuelRequestTimeoutSeconds = 20.0f;
    private const float DuelDurationSeconds = 45.0f;

    private static readonly string[] AllowedWeapons =
    {
        "weapon_ak47",
        "weapon_m4a1",
        "weapon_awp",
        "weapon_deagle",
        "weapon_ssg08"
    };

    private static readonly string[] UtilityPool =
    {
        "weapon_hegrenade",
        "weapon_flashbang",
        "weapon_smokegrenade",
        "weapon_molotov",
        "weapon_incgrenade"
    };

    private readonly Dictionary<ulong, DuelRequest> _pendingRequests = new();
    private readonly HashSet<ulong> _playersInDuel = new();
    private readonly Dictionary<string, DuelZone> _zones = new(StringComparer.OrdinalIgnoreCase);

    private bool _globalDuelModeEnabled;
    private string _selectedWeapon = AllowedWeapons[0];

    public override void Load(bool hotReload)
    {
        AddCommand("css_duel", "Lancer une demande de duel: !duel <nom>", CommandDuel);
        AddCommand("css_duel_accept", "Accepter un duel: !duel_accept", CommandAccept);
        AddCommand("css_duel_deny", "Refuser un duel: !duel_deny", CommandDeny);
        AddCommand("css_duel_cancel", "Annuler votre demande de duel", CommandCancel);

        AddCommand("css_duel_mode_start", "Activer le mode duel global", CommandDuelModeStart);
        AddCommand("css_duel_mode_stop", "Désactiver le mode duel global", CommandDuelModeStop);

        AddCommand("css_duel_zone_create", "Créer une zone de duel", CommandZoneCreate);
        AddCommand("css_duel_zone_delete", "Supprimer une zone de duel", CommandZoneDelete);
        AddCommand("css_duel_zone_list", "Lister les zones de duel", CommandZoneList);
        AddCommand("css_duel_zone_setspawn", "Définir un spawn de zone", CommandZoneSetSpawn);
    }

    private void CommandDuelModeStart(CCSPlayerController? caller, CommandInfo info)
    {
        if (!IsValidPlayer(caller) && caller is not null)
        {
            return;
        }

        if (!TryPickReadyZone(out var zoneName))
        {
            caller?.PrintToChat("\x07[DUEL]\x01 Aucune zone prête. Configurez d'abord une zone complète.");
            return;
        }

        if (info.ArgCount < 2)
        {
            ShowWeaponChoices(caller);
            caller?.PrintToChat("\x07[DUEL]\x01 Relancez: !duel_mode_start <index_arme|nom_arme>. Ex: !duel_mode_start 1");
            return;
        }

        if (!TryParseWeapon(info.GetArg(1), out var weapon))
        {
            ShowWeaponChoices(caller);
            caller?.PrintToChat("\x07[DUEL]\x01 Arme invalide.");
            return;
        }

        _globalDuelModeEnabled = true;
        _selectedWeapon = weapon;
        _pendingRequests.Clear();

        Server.PrintToChatAll($"\x07[DUEL]\x01 Mode duel global activé par admin. Arme commune: {_selectedWeapon}.");

        TryStartNextRound(previousZoneName: null);
    }

    private void CommandDuelModeStop(CCSPlayerController? caller, CommandInfo _)
    {
        _globalDuelModeEnabled = false;
        _pendingRequests.Clear();
        Server.PrintToChatAll("\x07[DUEL]\x01 Mode duel global désactivé.");
        caller?.PrintToChat("\x07[DUEL]\x01 Les duels en cours se terminent normalement, sans nouvelle relance auto globale.");
    }

    private void CommandDuel(CCSPlayerController? caller, CommandInfo info)
    {
        if (_globalDuelModeEnabled)
        {
            caller?.PrintToChat("\x07[DUEL]\x01 Mode duel global actif: les duels sont gérés automatiquement pour tout le serveur.");
            return;
        }

        if (!IsValidPlayer(caller, requireAlive: true))
        {
            return;
        }

        if (IsInDuel(caller!))
        {
            caller!.PrintToChat("\x07[DUEL]\x01 Vous êtes déjà engagé dans un duel.");
            return;
        }

        if (info.ArgCount < 2)
        {
            caller!.PrintToChat("\x07[DUEL]\x01 Usage: !duel <nom>.");
            return;
        }

        var target = FindPlayer(info.GetArg(1).Trim());
        if (!IsValidPlayer(target, requireAlive: true))
        {
            caller!.PrintToChat("\x07[DUEL]\x01 Joueur introuvable (ou mort). Utilisez un pseudo exact ou partiel unique.");
            return;
        }

        if (target!.SteamID == caller!.SteamID)
        {
            caller.PrintToChat("\x07[DUEL]\x01 Vous ne pouvez pas vous défier vous-même.");
            return;
        }

        if (IsInDuel(target))
        {
            caller.PrintToChat("\x07[DUEL]\x01 Ce joueur est déjà en duel.");
            return;
        }

        if (_pendingRequests.ContainsKey(target.SteamID))
        {
            caller.PrintToChat("\x07[DUEL]\x01 Ce joueur a déjà une demande en attente.");
            return;
        }

        var availableCount = GetAvailablePlayers().Count;
        var format = DuelFormatExtensions.SelectForPlayerCount(availableCount);
        if (format is null)
        {
            caller.PrintToChat("\x07[DUEL]\x01 Pas assez de joueurs disponibles pour démarrer un duel.");
            return;
        }

        if (!TryPickReadyZone(out var zoneName))
        {
            caller.PrintToChat("\x07[DUEL]\x01 Aucune zone prête. Créez une zone puis définissez 3 spawns pour team A et 3 pour team B.");
            return;
        }

        _pendingRequests[target.SteamID] = new DuelRequest(caller.SteamID, target.SteamID, format.Value, zoneName);

        caller.PrintToChat($"\x07[DUEL]\x01 Demande envoyée à {target.PlayerName} (format: {DuelFormatExtensions.Label(format.Value)}, zone: {zoneName}).");
        target.PrintToChat($"\x07[DUEL]\x01 {caller.PlayerName} vous défie ! Format: {DuelFormatExtensions.Label(format.Value)}, zone: {zoneName}. Tapez !duel_accept ou !duel_deny.");

        AddTimer(DuelRequestTimeoutSeconds, () => ExpireRequest(target.SteamID), TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void CommandAccept(CCSPlayerController? caller, CommandInfo _)
    {
        if (!IsValidPlayer(caller, requireAlive: true))
        {
            return;
        }

        if (!_pendingRequests.TryGetValue(caller!.SteamID, out var request))
        {
            caller.PrintToChat("\x07[DUEL]\x01 Vous n'avez aucune demande de duel en attente.");
            return;
        }

        var challenger = FindPlayer(request.ChallengerSteamId);
        if (!IsValidPlayer(challenger, requireAlive: true))
        {
            caller.PrintToChat("\x07[DUEL]\x01 Le joueur qui vous a défié n'est plus disponible.");
            _pendingRequests.Remove(caller.SteamID);
            return;
        }

        if (!_zones.TryGetValue(request.ZoneName, out var zone) || !zone.IsReady)
        {
            caller.PrintToChat("\x07[DUEL]\x01 Zone du duel indisponible ou incomplète.");
            _pendingRequests.Remove(caller.SteamID);
            return;
        }

        var availablePlayers = GetAvailablePlayers();
        if (!TryBuildDuelTeams(request.Format, challenger!, caller, availablePlayers, out var teamA, out var teamB))
        {
            caller.PrintToChat("\x07[DUEL]\x01 Impossible de lancer le duel: plus assez de joueurs disponibles.");
            _pendingRequests.Remove(caller.SteamID);
            return;
        }

        _pendingRequests.Remove(caller.SteamID);
        StartDuelRound(request.Format, request.ZoneName, teamA, teamB, isRematch: false);
    }

    private void CommandDeny(CCSPlayerController? caller, CommandInfo _)
    {
        if (!IsValidPlayer(caller))
        {
            return;
        }

        if (!_pendingRequests.TryGetValue(caller!.SteamID, out var request))
        {
            caller.PrintToChat("\x07[DUEL]\x01 Vous n'avez aucune demande de duel en attente.");
            return;
        }

        _pendingRequests.Remove(caller.SteamID);
        var challenger = FindPlayer(request.ChallengerSteamId);

        caller.PrintToChat("\x07[DUEL]\x01 Vous avez refusé le duel.");
        challenger?.PrintToChat($"\x07[DUEL]\x01 {caller.PlayerName} a refusé votre duel.");
    }

    private void CommandCancel(CCSPlayerController? caller, CommandInfo _)
    {
        if (!IsValidPlayer(caller))
        {
            return;
        }

        var requestToCancel = _pendingRequests.FirstOrDefault(x => x.Value.ChallengerSteamId == caller!.SteamID);
        if (requestToCancel.Equals(default(KeyValuePair<ulong, DuelRequest>)))
        {
            caller!.PrintToChat("\x07[DUEL]\x01 Vous n'avez aucune demande envoyée à annuler.");
            return;
        }

        _pendingRequests.Remove(requestToCancel.Key);

        var target = FindPlayer(requestToCancel.Key);
        caller!.PrintToChat("\x07[DUEL]\x01 Votre demande de duel a été annulée.");
        target?.PrintToChat($"\x07[DUEL]\x01 {caller.PlayerName} a annulé sa demande de duel.");
    }

    private void CommandZoneCreate(CCSPlayerController? caller, CommandInfo info)
    {
        if (!IsValidPlayer(caller))
        {
            return;
        }

        if (info.ArgCount < 2)
        {
            caller!.PrintToChat("\x07[DUEL]\x01 Usage: !duel_zone_create <nom_zone>");
            return;
        }

        var zoneName = info.GetArg(1).Trim();
        if (_zones.ContainsKey(zoneName))
        {
            caller!.PrintToChat("\x07[DUEL]\x01 Cette zone existe déjà.");
            return;
        }

        _zones[zoneName] = new DuelZone(zoneName);
        caller!.PrintToChat($"\x07[DUEL]\x01 Zone '{zoneName}' créée. Définissez ensuite 3 spawns pour A et 3 pour B.");
    }

    private void CommandZoneDelete(CCSPlayerController? caller, CommandInfo info)
    {
        if (!IsValidPlayer(caller))
        {
            return;
        }

        if (info.ArgCount < 2)
        {
            caller!.PrintToChat("\x07[DUEL]\x01 Usage: !duel_zone_delete <nom_zone>");
            return;
        }

        var zoneName = info.GetArg(1).Trim();
        if (!_zones.Remove(zoneName))
        {
            caller!.PrintToChat("\x07[DUEL]\x01 Zone introuvable.");
            return;
        }

        caller!.PrintToChat($"\x07[DUEL]\x01 Zone '{zoneName}' supprimée.");
    }

    private void CommandZoneList(CCSPlayerController? caller, CommandInfo _)
    {
        if (!IsValidPlayer(caller))
        {
            return;
        }

        if (_zones.Count == 0)
        {
            caller!.PrintToChat("\x07[DUEL]\x01 Aucune zone configurée.");
            return;
        }

        foreach (var zone in _zones.Values.OrderBy(z => z.Name))
        {
            caller!.PrintToChat($"\x07[DUEL]\x01 Zone {zone.Name} - A:{zone.TeamACount}/3 B:{zone.TeamBCount}/3 (prête: {(zone.IsReady ? "oui" : "non")})");
        }
    }

    private void CommandZoneSetSpawn(CCSPlayerController? caller, CommandInfo info)
    {
        if (!IsValidPlayer(caller))
        {
            return;
        }

        if (info.ArgCount < 7)
        {
            caller!.PrintToChat("\x07[DUEL]\x01 Usage: !duel_zone_setspawn <zone> <a|b> <1|2|3> <x> <y> <z> [pitch] [yaw] [roll]");
            return;
        }

        var zoneName = info.GetArg(1).Trim();
        if (!_zones.TryGetValue(zoneName, out var zone))
        {
            caller!.PrintToChat("\x07[DUEL]\x01 Zone introuvable. Créez-la d'abord avec !duel_zone_create.");
            return;
        }

        var team = ParseTeam(info.GetArg(2));
        if (team == DuelTeam.None)
        {
            caller!.PrintToChat("\x07[DUEL]\x01 Team invalide: utilisez a ou b.");
            return;
        }

        if (!int.TryParse(info.GetArg(3), out var slot) || slot is < 1 or > 3)
        {
            caller!.PrintToChat("\x07[DUEL]\x01 Slot invalide: utilisez 1, 2 ou 3.");
            return;
        }

        if (!TryReadFloat(info.GetArg(4), out var x) ||
            !TryReadFloat(info.GetArg(5), out var y) ||
            !TryReadFloat(info.GetArg(6), out var z))
        {
            caller!.PrintToChat("\x07[DUEL]\x01 Coordonnées invalides.");
            return;
        }

        var pitch = info.ArgCount >= 8 && TryReadFloat(info.GetArg(7), out var p) ? p : 0f;
        var yaw = info.ArgCount >= 9 && TryReadFloat(info.GetArg(8), out var yw) ? yw : 0f;
        var roll = info.ArgCount >= 10 && TryReadFloat(info.GetArg(9), out var rl) ? rl : 0f;

        zone.SetSpawn(team, slot - 1, new DuelSpawn(x, y, z, pitch, yaw, roll));
        caller!.PrintToChat($"\x07[DUEL]\x01 Spawn défini: zone {zoneName}, team {info.GetArg(2).ToUpperInvariant()}, slot {slot}.");
    }

    private void ShowWeaponChoices(CCSPlayerController? caller)
    {
        for (var i = 0; i < AllowedWeapons.Length; i++)
        {
            caller?.PrintToChat($"\x07[DUEL]\x01 Arme {i + 1}: {AllowedWeapons[i]}");
        }
    }

    private static bool TryParseWeapon(string raw, out string weapon)
    {
        weapon = string.Empty;

        if (int.TryParse(raw, out var idx) && idx >= 1 && idx <= AllowedWeapons.Length)
        {
            weapon = AllowedWeapons[idx - 1];
            return true;
        }

        var normalized = raw.Trim().ToLowerInvariant();
        var match = AllowedWeapons.FirstOrDefault(x => x.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return false;
        }

        weapon = match;
        return true;
    }

    private void ExpireRequest(ulong targetSteamId)
    {
        if (!_pendingRequests.TryGetValue(targetSteamId, out var request))
        {
            return;
        }

        _pendingRequests.Remove(targetSteamId);

        var challenger = FindPlayer(request.ChallengerSteamId);
        var target = FindPlayer(targetSteamId);

        challenger?.PrintToChat("\x07[DUEL]\x01 Votre demande de duel a expiré.");
        target?.PrintToChat("\x07[DUEL]\x01 La demande de duel a expiré.");
    }

    private void StartDuelRound(DuelFormat format, string zoneName, List<CCSPlayerController> teamA, List<CCSPlayerController> teamB, bool isRematch)
    {
        if (!_zones.TryGetValue(zoneName, out var zone) || !zone.IsReady)
        {
            return;
        }

        foreach (var player in teamA.Concat(teamB))
        {
            _playersInDuel.Add(player.SteamID);
        }

        TeleportTeamsToZone(zone, teamA, teamB);
        ApplySharedWeaponAndRandomUtility(teamA.Concat(teamB));

        var prefix = isRematch ? "Manche suivante" : "Duel";
        Server.PrintToChatAll($"\x07[DUEL]\x01 {prefix} {DuelFormatExtensions.Label(format)} sur {zoneName}: {FormatTeam(teamA)} \x01vs\x07 {FormatTeam(teamB)} (arme: {_selectedWeapon})");
        AddTimer(DuelDurationSeconds, () => EndDuel(zoneName, teamA, teamB), TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void EndDuel(string zoneName, List<CCSPlayerController> teamA, List<CCSPlayerController> teamB)
    {
        foreach (var player in teamA.Concat(teamB))
        {
            _playersInDuel.Remove(player.SteamID);
            if (IsValidPlayer(player))
            {
                player.PrintToChat("\x07[DUEL]\x01 Le duel est terminé.");
            }
        }

        TryStartNextRound(zoneName);
    }

    private void TryStartNextRound(string? previousZoneName)
    {
        var availablePlayers = GetAvailablePlayers();
        if (availablePlayers.Count < 2)
        {
            Server.PrintToChatAll("\x07[DUEL]\x01 Duel terminé: pas assez de joueurs pour une nouvelle manche.");
            return;
        }

        var nextFormat = DuelFormatExtensions.SelectForPlayerCount(availablePlayers.Count);
        if (nextFormat is null)
        {
            return;
        }

        if (!TryPickReadyZone(out var nextZoneName, preferredDifferentFrom: previousZoneName))
        {
            Server.PrintToChatAll("\x07[DUEL]\x01 Duel terminé: aucune zone prête disponible.");
            return;
        }

        if (!TryBuildRandomDuelTeams(nextFormat.Value, availablePlayers, out var teamA, out var teamB))
        {
            Server.PrintToChatAll("\x07[DUEL]\x01 Duel terminé: joueurs insuffisants pour reconstruire les équipes.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(previousZoneName) &&
            nextZoneName.Equals(previousZoneName, StringComparison.OrdinalIgnoreCase) &&
            _zones.Values.Count(z => z.IsReady) > 1)
        {
            Server.PrintToChatAll("\x07[DUEL]\x01 Changement de zone impossible cette manche, relance sur la même zone.");
        }

        StartDuelRound(nextFormat.Value, nextZoneName, teamA, teamB, isRematch: true);
    }

    private void ApplySharedWeaponAndRandomUtility(IEnumerable<CCSPlayerController> players)
    {
        foreach (var player in players)
        {
            var utility = UtilityPool[Random.Shared.Next(UtilityPool.Length)];
            TryGiveNamedItem(player, _selectedWeapon);
            TryGiveNamedItem(player, utility);
            player.PrintToChat($"\x07[DUEL]\x01 Equipement duel: {_selectedWeapon} + utility aléatoire ({utility}).");
        }
    }

    private static bool TryGiveNamedItem(CCSPlayerController player, string weaponName)
    {
        try
        {
            var method = player.GetType().GetMethod("GiveNamedItem", BindingFlags.Instance | BindingFlags.Public, binder: null, types: [typeof(string)], modifiers: null);
            if (method is not null)
            {
                method.Invoke(player, [weaponName]);
                return true;
            }

            var pawn = player.PlayerPawn?.Value;
            if (pawn is null)
            {
                return false;
            }

            var pawnMethod = pawn.GetType().GetMethod("GiveNamedItem", BindingFlags.Instance | BindingFlags.Public, binder: null, types: [typeof(string)], modifiers: null);
            if (pawnMethod is null)
            {
                return false;
            }

            pawnMethod.Invoke(pawn, [weaponName]);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TeleportTeamsToZone(DuelZone zone, List<CCSPlayerController> teamA, List<CCSPlayerController> teamB)
    {
        for (var i = 0; i < teamA.Count; i++)
        {
            var spawn = zone.TeamASpawns[i];
            if (spawn is not null)
            {
                TeleportPlayer(teamA[i], spawn.Value);
            }
        }

        for (var i = 0; i < teamB.Count; i++)
        {
            var spawn = zone.TeamBSpawns[i];
            if (spawn is not null)
            {
                TeleportPlayer(teamB[i], spawn.Value);
            }
        }
    }

    private static void TeleportPlayer(CCSPlayerController player, DuelSpawn spawn)
    {
        var pawn = player.PlayerPawn?.Value;
        if (pawn is null)
        {
            return;
        }

        pawn.Teleport(new Vector(spawn.X, spawn.Y, spawn.Z), new QAngle(spawn.Pitch, spawn.Yaw, spawn.Roll), new Vector(0, 0, 0));
    }

    private bool TryBuildDuelTeams(
        DuelFormat format,
        CCSPlayerController challenger,
        CCSPlayerController target,
        List<CCSPlayerController> availablePlayers,
        out List<CCSPlayerController> teamA,
        out List<CCSPlayerController> teamB)
    {
        teamA = new List<CCSPlayerController> { challenger };
        teamB = new List<CCSPlayerController> { target };

        var (teamASize, teamBSize) = DuelFormatExtensions.TeamSizes(format);
        var reserve = availablePlayers
            .Where(p => p.SteamID != challenger.SteamID && p.SteamID != target.SteamID)
            .OrderBy(_ => Guid.NewGuid())
            .ToList();

        while (teamA.Count < teamASize)
        {
            if (reserve.Count == 0)
            {
                return false;
            }

            teamA.Add(reserve[0]);
            reserve.RemoveAt(0);
        }

        while (teamB.Count < teamBSize)
        {
            if (reserve.Count == 0)
            {
                return false;
            }

            teamB.Add(reserve[0]);
            reserve.RemoveAt(0);
        }

        return true;
    }

    private static bool TryBuildRandomDuelTeams(
        DuelFormat format,
        List<CCSPlayerController> availablePlayers,
        out List<CCSPlayerController> teamA,
        out List<CCSPlayerController> teamB)
    {
        teamA = new List<CCSPlayerController>();
        teamB = new List<CCSPlayerController>();

        var (teamASize, teamBSize) = DuelFormatExtensions.TeamSizes(format);
        var needed = teamASize + teamBSize;
        if (availablePlayers.Count < needed)
        {
            return false;
        }

        var pool = availablePlayers.OrderBy(_ => Guid.NewGuid()).Take(needed).ToList();
        teamA = pool.Take(teamASize).ToList();
        teamB = pool.Skip(teamASize).Take(teamBSize).ToList();

        return true;
    }

    private bool TryPickReadyZone(out string zoneName, string? preferredDifferentFrom = null)
    {
        var readyZones = _zones.Values.Where(z => z.IsReady).Select(z => z.Name).ToList();
        if (readyZones.Count == 0)
        {
            zoneName = string.Empty;
            return false;
        }

        if (!string.IsNullOrWhiteSpace(preferredDifferentFrom))
        {
            var differentZones = readyZones
                .Where(z => !z.Equals(preferredDifferentFrom, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (differentZones.Count > 0)
            {
                zoneName = differentZones[Random.Shared.Next(differentZones.Count)];
                return true;
            }
        }

        zoneName = readyZones[Random.Shared.Next(readyZones.Count)];
        return true;
    }

    private static string FormatTeam(IEnumerable<CCSPlayerController> team)
    {
        return string.Join(", ", team.Select(p => p.PlayerName));
    }

    private List<CCSPlayerController> GetAvailablePlayers()
    {
        return Utilities.GetPlayers()
            .Where(p => IsValidPlayer(p, requireAlive: true) && !_playersInDuel.Contains(p.SteamID))
            .ToList();
    }

    private CCSPlayerController? FindPlayer(string fragment)
    {
        var players = Utilities.GetPlayers()
            .Where(p => IsValidPlayer(p))
            .ToList();

        var exact = players.FirstOrDefault(p => p.PlayerName.Equals(fragment, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var partialMatches = players
            .Where(p => p.PlayerName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return partialMatches.Count() == 1 ? partialMatches.First() : null;
    }

    private CCSPlayerController? FindPlayer(ulong steamId)
    {
        return Utilities.GetPlayers().FirstOrDefault(p => IsValidPlayer(p) && p.SteamID == steamId);
    }

    private bool IsInDuel(CCSPlayerController player)
    {
        return _playersInDuel.Contains(player.SteamID);
    }

    private static DuelTeam ParseTeam(string teamArg)
    {
        if (teamArg.Equals("a", StringComparison.OrdinalIgnoreCase))
        {
            return DuelTeam.TeamA;
        }

        if (teamArg.Equals("b", StringComparison.OrdinalIgnoreCase))
        {
            return DuelTeam.TeamB;
        }

        return DuelTeam.None;
    }

    private static bool IsValidPlayer(CCSPlayerController? player, bool requireAlive = false)
    {
        if (player is null || !player.IsValid || player.IsBot)
        {
            return false;
        }

        return !requireAlive || player.PawnIsAlive;
    }

    private static bool TryReadFloat(string raw, out float value)
    {
        return float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private readonly record struct DuelRequest(ulong ChallengerSteamId, ulong TargetSteamId, DuelFormat Format, string ZoneName);

    private readonly record struct DuelSpawn(float X, float Y, float Z, float Pitch, float Yaw, float Roll);

    private sealed class DuelZone
    {
        public DuelZone(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public DuelSpawn?[] TeamASpawns { get; } = new DuelSpawn?[3];
        public DuelSpawn?[] TeamBSpawns { get; } = new DuelSpawn?[3];

        public int TeamACount => TeamASpawns.Count(s => s is not null);
        public int TeamBCount => TeamBSpawns.Count(s => s is not null);
        public bool IsReady => TeamACount == 3 && TeamBCount == 3;

        public void SetSpawn(DuelTeam team, int index, DuelSpawn spawn)
        {
            if (team == DuelTeam.TeamA)
            {
                TeamASpawns[index] = spawn;
                return;
            }

            TeamBSpawns[index] = spawn;
        }
    }

    private enum DuelTeam
    {
        None = 0,
        TeamA = 1,
        TeamB = 2
    }

    private enum DuelFormat
    {
        OneVsOne,
        OneVsTwo,
        TwoVsTwo,
        ThreeVsTwo
    }

    private static class DuelFormatExtensions
    {
        public static DuelFormat? SelectForPlayerCount(int playerCount)
        {
            if (playerCount >= 5)
            {
                return DuelFormat.ThreeVsTwo;
            }

            if (playerCount == 4)
            {
                return DuelFormat.TwoVsTwo;
            }

            if (playerCount == 3)
            {
                return DuelFormat.OneVsTwo;
            }

            if (playerCount == 2)
            {
                return DuelFormat.OneVsOne;
            }

            return null;
        }

        public static (int TeamA, int TeamB) TeamSizes(DuelFormat format)
        {
            return format switch
            {
                DuelFormat.OneVsOne => (1, 1),
                DuelFormat.OneVsTwo => (1, 2),
                DuelFormat.TwoVsTwo => (2, 2),
                DuelFormat.ThreeVsTwo => (3, 2),
                _ => (1, 1)
            };
        }

        public static string Label(DuelFormat format)
        {
            return format switch
            {
                DuelFormat.OneVsOne => "1v1",
                DuelFormat.OneVsTwo => "1v2",
                DuelFormat.TwoVsTwo => "2v2",
                DuelFormat.ThreeVsTwo => "3v2",
                _ => "1v1"
            };
        }
    }
}
