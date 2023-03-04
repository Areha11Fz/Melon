fn main() {
    let profile = std::env::var("PROFILE").unwrap();
    match profile.as_str() {
        "release" => println!("cargo:rustc-link-arg=/DEBUG:NONE"),
        _ => (),
    }
}