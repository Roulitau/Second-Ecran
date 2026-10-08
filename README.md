# Second écran USB (PC Windows → tablette Android)

Le PC capture un écran (vrai écran étendu via un écran virtuel) et l'envoie à la tablette **par le câble USB**.
Le toucher de la tablette pilote la souris du PC.

**Aucun débogage USB nécessaire** : on utilise le *Partage par USB* de la tablette (réseau à travers le câble).
Le serveur n'accepte que les adresses privées (câble, réseau maison), jamais Internet.

```
windows/  SecondEcran.exe (C# / .NET 8, WPF) : serveur + interface
android/  app Android (Kotlin) : affiche le flux, envoie le toucher
pc/       ancienne version Python + faux clients de test (facultatif)
.github/  build.yml : GitHub Actions compile l'exe et l'APK
```

## 1. Récupérer l'exe et l'APK (GitHub Actions)
À chaque `git push`, GitHub compile. Onglet **Actions** → dernier run (✓ bleu) → *Artifacts* en bas :
- **SecondEcran-Windows** : `SecondEcran.exe` + ffmpeg + adb dans le même dossier. Rien à installer.
- **SecondEcran-Android-apk** : `app-debug.apk` à installer sur la tablette.

⚠ Télécharge/extrais **hors du dossier du projet** (ex. Bureau\Fichiers-SecondEcran). GitHub refuse les fichiers > 100 Mo.
⚠ Windows peut afficher « SmartScreen » : *Informations complémentaires* → *Exécuter quand même* (exe non signé).

## 2. Installation (une seule fois)
1. **Tablette** : installe l'APK (autorise « sources inconnues » si demandé).
2. **PC** : lance `SecondEcran.exe`.
3. **Écran virtuel** (pour un vrai écran étendu) : dans l'app, carte *Écran virtuel* → si « Non installé » :
   `winget install --id=VirtualDrivers.Virtual-Display-Driver -e`, puis rouvre l'app.

## 3. Utilisation (à chaque fois)
1. Branche le câble USB (câble **données**, pas seulement charge).
2. Tablette : Paramètres → Partage de connexion → **Partage par USB** (ON).
3. PC : carte *Écran virtuel* → **Activer** (UAC à accepter) ; choisis l'écran à envoyer.
4. PC : **▶ Démarrer**.
5. Tablette : ouvre l'app **Second écran**. Elle cherche le PC toute seule (🔎 puis flux).
6. Windows → Paramètres → Affichage → **Étendre ces affichages**, résolution proche de la tablette.

Retour arrière / arrêt : **■ Arrêter**. Bouton **🔌 Rebrancher le câble** si tu débranches/rebranches.
Quitter le flux sur la tablette : bouton Retour (écran de connexion avec adresse manuelle).

## 4. Réglages (fenêtre Windows)
- **Écran** : lequel envoyer (par défaut le dernier non principal).
- **Images/s** : 60 par défaut. **Débit** : qualité/bande passante. **Largeur max** : 1920/1280 si le PC peine.
- **Encodeur** : Auto (test au démarrage : NVENC > Intel QSV > AMD AMF > processeur x264). Le journal indique le choix.

## 5. Gestes
Toucher = clic · glisser = maintenir + déplacer · appui long = clic droit · deux doigts = défilement.

## 6. Veille / reconnexion
- Le PC reste éveillé tant que le serveur tourne.
- Si le PC dort, le câble est rebranché ou le serveur redémarre, l'app réessaie seule toutes les 2 s.
- Tablette en veille / app en arrière-plan : coupe, puis reprend au retour.

## 7. Modes de connexion
| Mode | Besoin | Notes |
|---|---|---|
| **Partage par USB** (défaut) | rien d'activé en dev | le PC reçoit une IP du câble (ex. 192.168.42.x) ; l'app scanne ce réseau |
| `adb reverse` | Débogage USB | essayé aussi automatiquement, via 127.0.0.1 |
| Navigateur (secours) | Chrome | `http://<IP du PC>:5555` ; H.264 WebCodecs, sinon JPEG |

L'IP du PC est affichée dans la carte *Connexion par câble USB* (et dans le journal).
Dans l'app, tu peux aussi saisir l'adresse à la main (« auto » = recherche seule).

## 8. Dépannage
- **« PC introuvable » sur la tablette** : serveur démarré ? *Partage par USB* activé ? Pare-feu Windows : autorise `SecondEcran.exe` sur les réseaux privés (et classe le réseau du câble en « Privé »).
- **Pas de « Partage par USB »** (tablette sans cette option) : active le point d'accès Wi-Fi du PC (Windows → Point d'accès mobile) et saisis l'IP du PC (192.168.137.1) dans l'app.
- **Pas de 2ᵉ écran dans Windows** : Écran virtuel non installé ou désactivé (carte *Écran virtuel*). Après activation, **redémarre le serveur**.
- **ffmpeg introuvable** : garde `ffmpeg.exe` à côté de `SecondEcran.exe`.
- **Latence** : regarde l'encodeur dans le journal (x264 = plus lent) ; baisse la largeur max ; utilise un câble USB 3 de qualité.
- **Souris au mauvais endroit** : l'écran choisi n'est pas celui affiché sur la tablette.
- **Tablette et PC sur des sous-réseaux différents (Wi-Fi)** : normal, utilise le câble (partage USB).
- **Push GitHub refusé (> 100 Mo)** : un zip/exe est dans le dépôt → `git reset --soft origin/main`, sors les fichiers du dossier, relis `.gitignore` (`*.zip`, `*.exe`, `*.dll`), recommit.

## 9. Développement
- Windows : `dotnet publish windows/SecondEcran.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`
- Android : `cd android && ./gradlew assembleDebug`
- Logique serveur testable sans Windows : `windows/tests/CoreTest` (source de test, ffmpeg requis) puis `python pc/test_client.py 127.0.0.1 5599 5`.
- Protocole (port 5555) : 1er octet `0x10` → binaire `[type:1][len:4 BE][charge]` (INFO 0x01, VIDEO 0x02, HELLO 0x10, TOUCH 0x11) ; sinon HTTP/WebSocket pour la page web.
