# BitChat pour macOS — 0.1.9 preview

Application native Swift/SwiftUI, universelle Intel et Apple Silicon, pour macOS 13 ou ultérieur. Le runtime et Tor sont embarqués : aucun SDK ni gestionnaire de paquets à installer.

## Installation

1. Ouvrir `BitChat-macOS-universal-0.1.9.dmg`.
2. Glisser `BitChat.app` vers `Applications`.
3. Pour remplacer une version existante, quitter BitChat avec Cmd+Q avant de remplacer l’application. Les données utilisateur ne font pas partie du DMG.
4. Ouvrir BitChat depuis Applications. Autoriser Bluetooth et les notifications pour utiliser ces fonctions; le microphone est nécessaire pour les vocaux.

La distribution n’est ni signée par un certificat Apple Developer ID, ni notariée. Si macOS bloque l’ouverture, utiliser Réglages Système > Confidentialité et sécurité > Ouvrir quand même après une première tentative. Le paquet possède seulement le scellement ad hoc requis par Apple Silicon; il ne nécessite aucune clé de développeur. Ne pas désactiver Gatekeeper globalement.

Le ZIP contient la même application, à déposer dans Applications.

## Comportement

- Thème natif Liquid Glass par défaut pour une nouvelle installation. L’ancienne apparence terminal reste accessible dans les paramètres sous « Hack mode ». Un thème déjà choisi est conservé.
- Fermer la fenêtre laisse l’application fonctionner. Cliquer sur son icône dans le Dock rouvre la fenêtre; Cmd+Q quitte complètement l’application.
- Cette version utilise le client Swift du dépôt : elle n’est pas l’exécutable Windows et n’inclut pas automatiquement ses ajouts en C#/WPF (nouvelle visionneuse Windows, son téléchargeur de mise à jour, ses notifications Windows, etc.).
- Les corrections récentes de réception côté Windows sont distribuées dans l’installeur Windows 0.1.8. Le protocole Swift natif est conservé ici.

## Reproduire le paquet

Depuis un Mac disposant de Xcode :

```sh
bash scripts/package-macos.sh
```

Les variables `BITCHAT_MAC_BUILD_DIR`, `BITCHAT_MAC_OUTPUT_DIR` et `BITCHAT_MAC_VERSION` permettent de choisir les chemins et la version. Le script compile les deux architectures, vérifie leur présence, scelle localement l’application, puis produit DMG, ZIP et empreintes SHA-256. `BUILD-INFO-macOS.txt` identifie le commit source et Xcode.

Depuis 0.1.9, le script exige des modifications commitées, compile une archive Git propre dans un répertoire temporaire neuf et vérifie une liste explicite des ressources autorisées. Les attributs étendus du Mac de compilation ne sont pas copiés. Les releases officielles sont construites sur un runner GitHub neuf, sans le profil personnel du développeur. Les rapports `SECURITY-*.json` ne contiennent que des chemins relatifs, des nombres et des empreintes.

Une réinstallation conserve les données déjà présentes dans le profil macOS. Retrouver ses conversations après une mise à jour ne signifie donc pas qu’elles étaient intégrées au DMG. La construction du paquet n’ouvre pas l’application et ne lit pas les dossiers de conversations ou le trousseau utilisateur.
