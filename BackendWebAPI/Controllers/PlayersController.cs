using BackendWebAPI.Data;
using BackendWebAPI.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendWebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PlayersController(AppDbContext db) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<IEnumerable<Player>>> GetAll()
        => await db.Players.AsNoTracking().ToListAsync();

    // Find players by Id
    [HttpGet("{id:guid}")] public async Task<ActionResult<Player>> Get(Guid id)
        => await db.Players.FindAsync(id) is { } p ? p : NotFound();
    
    // List all players in the descending order
    [HttpGet("leaderboard")]
    public async Task<IEnumerable<object>> Leaderboard([FromQuery] int top = 10)
        => await db.Players.AsNoTracking()
            .OrderByDescending(p => p.Elo).Take(top)
            .Select(p => new { p.PlayerId, p.Username, p.Elo }).ToListAsync();

    // Create a new player
    [HttpPost] public async Task<ActionResult<Player>> Create(Player p)
    {
        db.Players.Add(p); await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = p.PlayerId }, p);
    }
    // Update an existing player
    [HttpPut("{id:guid}")] public async Task<IActionResult> Update(Guid id, Player p)
    {
        if (id != p.PlayerId) return BadRequest();
        db.Entry(p).State = EntityState.Modified;
        await db.SaveChangesAsync();
        return NoContent();
    }
    // Delete an existing player
    [HttpDelete("{id:guid}")] public async Task<IActionResult> Delete(Guid id)
    {
        var p = await db.Players.FindAsync(id);
        if (p is null) return NotFound();
        db.Players.Remove(p); await db.SaveChangesAsync();
        return NoContent();
    }
}