# VRC Hand Gesture Creator

Unity エディタ上で VRChat アバターのハンドジェスチャー（ピース、グー、指差しなど）を作成・保存し、他のアバターへ移植するためのエディタ拡張です。

## 導入
1. このフォルダ (`HandGestureCreator`) をプロジェクトの `Packages/` か `Assets/` 以下にコピー
2. メニュー **Tools > Hand Gesture Creator** で専用ウィンドウを開く

## 使い方
1. シーン上のアバター（Humanoid の Animator）をウィンドウにドラッグ
2. 左手 / 右手 / 両手 を選択
3. クイックプリセット（Open / Fist / Point / Peace / ThumbsUp / RockNRoll / HandGun）から始めるか、
   各指のスライダー（第1〜3関節の曲げ・開き、または「全体の曲げ」）で調整
   - ライブプレビューでシーン上のアバターの指がリアルタイムに動きます
   - 「手にフォーカス」でシーンビューを手に寄せられます
4. 名前を付けて **ライブラリに新規保存**
5. **.anim として書き出し** → VRChat の FX / Gesture レイヤーのジェスチャーステートに割り当て

「ポーズを元に戻す」かウィンドウを閉じると、アバターは編集前の状態に戻ります。

## 他プロジェクトでの利用
ライブラリは JSON でプロジェクト外に保存されます（既定:
`%LOCALAPPDATA%/VRCHandGestureCreator/Gestures`）。保存先は EditorPrefs に記録されるため、
どの Unity プロジェクトで開いても同じライブラリが表示されます。JSON 単体での書き出し/読み込みも可能です。

## 他アバターへの移植
ジェスチャーは **Humanoid のマッスル値**（-1〜1）で保持しているため、ボーンの長さや向きが違うアバターでも
同じ手の形になります。
- 別アバターを Animator 欄に入れてライブラリから「読込」→ `.anim` 書き出し
- 既存アバターの手アニメーションを **.anim から読み込み** でジェスチャー化 → 別アバター用に保存
- シーン上で手のポーズを付けたアバターから **アバターの現在の指を読み取る** で取り込み

## ファイル構成
| ファイル | 役割 |
|---|---|
| `Editor/HandGestureData.cs` | ジェスチャーデータ、マッスル名の対応、組み込みプリセット |
| `Editor/HandGestureLibrary.cs` | プロジェクト共通ライブラリ（JSON 保存） |
| `Editor/HandGestureAnimUtility.cs` | アバターへの適用・読取、AnimationClip 変換 |
| `Editor/HandGestureWindow.cs` | 専用エディタウィンドウ |
