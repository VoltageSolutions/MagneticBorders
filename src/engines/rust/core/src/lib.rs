//! Domain layer (`mb-core`): pure geometry and the seam algorithm. No OS types, no I/O, no global state;
//! every function is deterministic, so the fixture runner and the unit tests drive it directly.
//! Depends on nothing; `mb-app` and `mb-fixtures` depend on it.
mod geom;
mod seam;

pub use geom::{Config, Edge, Rect, WinInfo};
pub use seam::{build_seam, propagate, Member, Plan, Seam, Side};
