# Second écran USB (PC Windows → tablette Android)

Le PC capture un écran avec ffmpeg et l'envoie à la tablette **par le câble USB** (`adb reverse`).
Le serveur n'écoute qu'en local (`127.0.0.1`) : rien n'est exposé sur le Wi-Fi, pas de code d'accès à gérer.

```
pc/       secondscreen.py (serveur + interface), webpage.py (page web), test_*.py, build_exe.bat
android/  projet Android Studio (app optionnelle, même protocole)
```

## Fabriquer l'exe et l'APK (GitHub Actions)
Le dépôt contient `.github/workflows/build.yml` : à chaque `git push`, GitHub compile
- **SecondEcran-Windows** : `SecondEcran.exe` + ffmpeg + adb dans un dossier (rien à installer),
- **SecondEcran-Android-apk** : `app-debug.apk` à installer sur la tablette.
Onglet **Actions** du dépôt → dernier run → *Artifacts* en bas. (Windows peut afficher « SmartScreen » :
Informations complémentaires → Exécuter quand même, l'exe n'est pas signé.)

## Installation (une fois, si tu utilises le .py)
1. Python 3.10+ (python.org). 2. `winget install Gyan.FFmpeg`. 3. `winget install Google.PlatformTools` (adb).
Rouvre le terminal ensuite. Tablette : Options pour les développeurs → **Débogage USB**.

## Utilisation
1. Branche le câble (câble « données », pas seulement charge), accepte la fenêtre sur la tablette.
2. `cd pc` puis `python secondscreen.py` → **▶ Démarrer** (il lance `adb reverse` tout seul).
3. Tablette, Chrome : `http://127.0.0.1:5555`, puis **⛶ Plein écran**.
Débranché/rebranché : bouton **🔌 Rebrancher le câble**.

## Latence
- La page utilise **H.264 par WebCodecs** (décodage matériel) quand Chrome le permet — c'est possible
  car `127.0.0.1` est un contexte sécurisé. Sinon elle retombe sur JPEG toute seule.
  Le journal indique le format : « Format navigateur : H.264 » ou « JPEG ».
- 60 images/s par défaut. Baisse « Largeur max » (1920/1280) si le PC peine.
- Encodeur « auto » : NVENC / QSV / AMF si dispo, sinon x264 (plus lent). Le journal affiche l'encodeur choisi.

## Vrai écran étendu
Il faut que Windows voie un 2ᵉ écran : driver d'écran virtuel (*Virtual Display Driver*, GitHub) ou
dongle HDMI « dummy plug ». Puis Affichage → **Étendre**, à la résolution de la tablette.
Par défaut le serveur prend le dernier écran non principal.

## Gestes
Toucher = clic · glisser = maintenir + déplacer · appui long = clic droit · deux doigts = défilement.

## Dépannage
- **adb introuvable** : étape 3 d'installation, puis rouvre le terminal.
- **Appareil non détecté** : câble données, débogage USB, fenêtre d'autorisation acceptée (`adb devices` doit dire `device`).
- **Page inaccessible** : serveur démarré ? Rebranche avec le bouton 🔌.
- **Toujours de la latence** : regarde le format dans le journal ; si JPEG, mets Chrome à jour ; essaie 1920 de largeur max.
- **ffmpeg introuvable** : rouvre le terminal après l'installation.
- **Souris au mauvais endroit** : l'écran choisi n'est pas celui affiché.

## Tests sans tablette
```
python secondscreen.py --test-source --port 5599
python test_web_client.py 127.0.0.1 5599 5
```
App Android : elle se connecte seule à 127.0.0.1 au lancement (câble branché, serveur démarré).
