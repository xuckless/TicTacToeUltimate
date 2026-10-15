# Button

Chunky 3D button with a 4px ledge that collapses on press.

- **Consumer provides:** a label (UPPERCASE via the `label` style) and one variant: `primary`, `secondary`, `ghost` or `danger`. The `hero` modifier makes the big Play card on Home: a `label` line above a `title`-size line.
- **Do:** use one `primary` per screen for the main action. Use `danger` only for Resign confirmation.
- **Don't:** place two primary buttons side by side, or use `danger` for ordinary cancel actions (use `ghost`).
