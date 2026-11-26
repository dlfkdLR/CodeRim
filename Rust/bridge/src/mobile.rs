//! Policy functions shared by the Xcode iOS static library and host contract tests.
extern crate alloc;

#[path = "mobile_follow.rs"]
mod follow;
#[path = "mobile_reading.rs"]
mod reading;
#[path = "mobile_registration.rs"]
mod registration;
#[path = "mobile_selection.rs"]
mod selection;
