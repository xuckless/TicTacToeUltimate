# Board

The 3x3 board. Every cell is a tappable chunky tile.

- **Consumer provides:** 9 cell states (`empty`, `x`, `o`), the index of the piece that vanishes next (rendered `fading`), whether it is your turn (cells are disabled when it isn't), and the winning line, if any.
- Show the `fading` piece only once the move window is full. Show its dashed outline in its own piece color.
- The win line uses the winner's piece color and triggers the impact frame.
- **Don't:** recolor X or O for cosmetics without keeping cyan/orange contrast, or show the board without glyphs (color alone never tells the pieces apart).
