# A-Rank Hunt Tour Assistant v0.1.0

日本語名：**Aモブハントツアー補助ツール**

Aモブハントツアーの探索・記録・MAP共有を補助するDalamudプラグインです。

## 主な機能
- 対象エリア：漆黒 / 暁月 / 黄金
- データセンター → ワールド → 追加ディスク → MAP の順で選択
- JP DC：Elemental / Gaia / Mana / Meteor
- Aモブ候補地点の巡回探索
- モブ単位巡回 / 最寄り座標巡回
- 追加ディスク内MAP巡回 / 選択DC内ワールド巡回
- Aモブ発見時の探索記録
- プラグイン有効中のAモブ討伐記録
- 探索記録からMOBハント用リストへ手動追加
- MOBハント用リストの手動並び替え・個別削除・一括クリア
- 記録クリックでチャット入力欄へ `MOB名 (X,Y) <flag>` を入力
- スキップ記録 / 自動スキップ
- 記録データの保存先設定

## コマンド
`/arankhunttour`

## 必要プラグイン
- vnavmesh

## ビルド
`build.bat` を実行してください。

成功時は次を自動作成します。
- `release\ARankHuntTourAssistant\`：配布ファイル
- `release\ARankHuntTourAssistant_v0.1.0.zip`：GitHub Release等に添付する配布ZIP
- `Z:\ARankHuntTourAssistant\`：実機テスト用コピー

ビルド成功・失敗のどちらでも画面は自動で閉じません。
