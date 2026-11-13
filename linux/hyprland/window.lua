-- Bambu Bar compact main window (managed by install-hyprland-integration.sh).
-- Quick view and camera windows have different titles and keep their own placement.
hl.window_rule({
  name = "bambu-bar-compact-main",
  match = { class = "^dev\\.rolottr\\.BambuBar$", title = "^Bambu Bar$" },
  float = true,
  center = true,
  size = "960 700",
})
