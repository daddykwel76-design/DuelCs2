# DuelCs2

Plugin CounterStrikeSharp pour CS2 permettant d'organiser des duels dynamiques avec les joueurs présents sur le serveur.

## Formats automatiques

Le format est choisi automatiquement selon le nombre de joueurs vivants et disponibles:

- 2 joueurs: `1v1`
- 3 joueurs: `1v2`
- 4 joueurs: `2v2`
- 5 joueurs ou plus: `3v2`

## Configuration des zones (obligatoire)

La configuration commence par la **création d'une zone**, puis la définition de **3 spawns par équipe**:

1. `!duel_zone_create <nom_zone>`
2. Définir les spawns de la zone:
   - `!duel_zone_setspawn <nom_zone> a 1 <x> <y> <z> [pitch] [yaw] [roll]`
   - `!duel_zone_setspawn <nom_zone> a 2 <x> <y> <z> [pitch] [yaw] [roll]`
   - `!duel_zone_setspawn <nom_zone> a 3 <x> <y> <z> [pitch] [yaw] [roll]`
   - `!duel_zone_setspawn <nom_zone> b 1 <x> <y> <z> [pitch] [yaw] [roll]`
   - `!duel_zone_setspawn <nom_zone> b 2 <x> <y> <z> [pitch] [yaw] [roll]`
   - `!duel_zone_setspawn <nom_zone> b 3 <x> <y> <z> [pitch] [yaw] [roll]`

Commandes utiles:

- `!duel_zone_list`
- `!duel_zone_delete <nom_zone>`

Une zone est considérée **prête** quand les 6 spawns sont définis.

## Mode duel global (admin)

Quand un admin lance le mode duel global:

- le mode duel est appliqué à tous les joueurs vivants présents;
- un choix d'arme commune est proposé puis appliqué à tous;
- à chaque nouvelle manche duel, chaque joueur reçoit une utility aléatoire.

Commandes:

- `!duel_mode_start` : affiche la liste des armes disponibles.
- `!duel_mode_start <index|weapon_name>` : démarre le mode global avec l'arme choisie.
  - Exemples: `!duel_mode_start 1` ou `!duel_mode_start weapon_awp`
- `!duel_mode_stop` : arrête le mode duel global.

Armes disponibles par défaut:

1. `weapon_ak47`
2. `weapon_m4a1`
3. `weapon_awp`
4. `weapon_deagle`
5. `weapon_ssg08`

## Enchaînement automatique des manches

À la fin d'un duel, le plugin relance automatiquement une nouvelle manche si possible:

- la prochaine manche se lance sur **une autre zone** s'il en existe plusieurs prêtes ;
- les équipes sont **recomposées aléatoirement** parmi les joueurs vivants présents ;
- le format (`1v1`, `1v2`, `2v2`, `3v2`) est recalculé selon le nombre de joueurs disponibles.

## Commandes duel classique

- `!duel <nom>` : envoie une demande de duel (sur une zone prête choisie aléatoirement).
- `!duel_accept` : accepte la demande reçue et démarre le duel.
- `!duel_deny` : refuse la demande reçue.
- `!duel_cancel` : annule une demande envoyée.

## Build

```bash
dotnet build
```
