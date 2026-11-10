; Variables every size shares. Included from [Rainmeter] of Small/Medium/Large.ini.

[Variables]
; The state file this widget draws (windows/docs/widget-state.md). An instance stub sets it to the absolute path
; of its printer's file; a skin loaded on its own reads the tray's printer from the app's State folder
; (%LOCALAPPDATA%\Bambu Bar\Widgets\State, next to the Skins folder).
StateFile=#@#..\..\..\State\default.json
; Transparent room around the card for its shadow, in logical px (Images\shadow-<size>.png is drawn for it).
Margin=16
@Include=#@#App.inc
; The sections the meters inherit from.
@Include2=#@#Styles.inc
