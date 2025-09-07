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

    [HttpGet("{id:guid}")] public async Task<ActionResult<Player>> Get(Guid id)
        => await db.Players.FindAsync(id) is { } p ? p : NotFound();

    [HttpGet("leaderboard")]
    public async Task<IEnumerable<object>> Leaderboard([FromQuery] int top = 10)
        => await db.Players.AsNoTracking()
            .OrderByDescending(p => p.Elo).Take(top)
            .Select(p => new { p.PlayerId, p.Username, p.Elo }).ToListAsync();

    [HttpPost] public async Task<ActionResult<Player>> Create(Player p)
    {
        db.Players.Add(p); await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = p.PlayerId }, p);
    }

    [HttpPut("{id:guid}")] public async Task<IActionResult> Update(Guid id, Player p)
    {
        if (id != p.PlayerId) return BadRequest();
        db.Entry(p).State = EntityState.Modified;
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:guid}")] public async Task<IActionResult> Delete(Guid id)
    {
        var p = await db.Players.FindAsync(id);
        if (p is null) return NotFound();
        db.Players.Remove(p); await db.SaveChangesAsync();
        return NoContent();
    }
}