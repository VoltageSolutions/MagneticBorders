//! Domain layer (`mb-core`): pure geometry, the seam algorithm, and magnetic attraction. No OS types, no I/O, no
//! global state; every function is deterministic, so the fixture runner and the unit tests drive it directly.
//! Depends on nothing; `mb-app` and `mb-fixtures` depend on it.
mod attraction;
mod geom;
mod pointer_drag;
mod seam;

pub use attraction::{
    make_target, screen_side_targets, Attraction, AttractionConfig, AttractionEvent, ReleaseReason, SideTargets, Span,
    TargetKind, TargetWindow,
};
pub use geom::{Config, Edge, Point, Rect, WinInfo};
pub use pointer_drag::PointerDrag;
pub use seam::{build_seam, propagate, Member, Plan, Seam, Side};
