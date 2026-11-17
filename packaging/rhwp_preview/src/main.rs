//! Minimal HWP/HWPX page renderer for M-Dir, built on rhwp core.
//!
//! Supports:
//!   rhwp-preview.exe --version
//!   rhwp-preview.exe export-png <file> [-p N] [--scale S] [-o out.png] [--json]
//!   rhwp-preview.exe export-pdf <file> [-p N] [-o out.pdf] [--json]
//!   rhwp-preview.exe --worker   (line-delimited JSON stdin/stdout session)

use std::env;
use std::fs;
use std::io::{self, BufRead, Write};
use std::path::{Path, PathBuf};
use std::process;
use std::sync::{Arc, OnceLock};

use resvg::{tiny_skia, usvg};
use rhwp::paint::RenderProfile;
use rhwp::parser::{detect_format, FileFormat};
use rhwp::renderer::font_environment::FontEnvironment;
use rhwp::renderer::pdf::{apply_pdf_font_options, PdfExportOptions};
use rhwp::DocumentCore;
use serde::Deserialize;

const MAX_INPUT_BYTES: u64 = 128 * 1024 * 1024;
const DEFAULT_PNG_SCALE: f32 = 2.0;
const MAX_RASTER_DIM: u32 = 4096;

static BASE_FONTDB: OnceLock<Arc<usvg::fontdb::Database>> = OnceLock::new();

fn base_fontdb(options: &PdfExportOptions) -> Arc<usvg::fontdb::Database> {
    Arc::clone(BASE_FONTDB.get_or_init(|| {
        let mut db = usvg::fontdb::Database::new();
        db.load_system_fonts();
        db.set_serif_family(options.fallback_serif.as_str());
        db.set_sans_serif_family(options.fallback_sans.as_str());
        db.set_monospace_family(options.fallback_mono.as_str());
        Arc::new(db)
    }))
}

fn build_fontdb(options: &PdfExportOptions) -> Arc<usvg::fontdb::Database> {
    let base = base_fontdb(options);
    if options.font_paths.is_empty() {
        return base;
    }
    let mut db = (*base).clone();
    for path in &options.font_paths {
        if path.is_dir() {
            db.load_fonts_dir(path);
        } else if path.is_file() {
            let _ = db.load_font_file(path);
        }
    }
    Arc::new(db)
}

fn svg_image_href_resolver() -> usvg::ImageHrefResolver<'static> {
    let default_data = usvg::ImageHrefResolver::default_data_resolver();
    usvg::ImageHrefResolver {
        resolve_data: Box::new(move |mime, data, opts| match mime {
            "image/svg+xml" => usvg::Tree::from_data(&data, opts).ok().map(usvg::ImageKind::SVG),
            _ => default_data(mime, data, opts),
        }),
        // Never allow network or arbitrary host file fetches from rendered document SVGs.
        resolve_string: Box::new(|_, _| None),
    }
}

struct LoadedDoc {
    doc: DocumentCore,
    pdf_options: PdfExportOptions,
    fontdb: Arc<usvg::fontdb::Database>,
    page_count: u32,
}

fn load_for_preview(path: &Path) -> Result<LoadedDoc, (String, bool)> {
    let meta = fs::metadata(path).map_err(|e| (format!("cannot stat {}: {e}", path.display()), false))?;
    if !meta.is_file() {
        return Err(("not a regular file".to_string(), false));
    }
    if meta.len() > MAX_INPUT_BYTES {
        return Err(("file exceeds 128 MiB limit".to_string(), false));
    }
    let data = fs::read(path).map_err(|e| (format!("cannot read {}: {e}", path.display()), false))?;
    let mut doc = DocumentCore::new_empty();
    if let Err(err) = doc.load_bytes(&data, None) {
        let msg = err.to_string();
        let encrypted = msg.contains("비밀번호") || msg.contains("ENCRYPT");
        return Err((msg, encrypted));
    }

    if !matches!(detect_format(&data), FileFormat::Hml) {
        if let Some(parent) = path.parent() {
            let _ = doc.populate_external_images_from_dir(parent);
        }
    }

    let mut font_paths: Vec<PathBuf> = Vec::new();
    let mut pdf_options = PdfExportOptions::default();

    // Resolve installed Windows/cached fonts cleanly without any network access.
    if env::var_os("RHWP_WEBFONT_OFFLINE").is_none() {
        env::set_var("RHWP_WEBFONT_OFFLINE", "1");
    }
    if let Ok(supply) = rhwp::renderer::native_webfonts::prepare(&doc.document().doc_info, &mut font_paths) {
        font_paths.extend(supply.paths);
        if let Ok(Some(env_cfg)) = FontEnvironment::from_native_document_substitutions(
            &doc.document().doc_info,
            &font_paths,
        ) {
            let _ = doc.set_font_environment(Some(env_cfg));
        }
    } else if let Ok(Some(env_cfg)) = FontEnvironment::from_native_document_substitutions(
        &doc.document().doc_info,
        &font_paths,
    ) {
        let _ = doc.set_font_environment(Some(env_cfg));
    }

    pdf_options.font_paths = font_paths;
    let fontdb = build_fontdb(&pdf_options);
    let page_count = doc.page_count();
    if page_count == 0 {
        return Err(("document has 0 pages".to_string(), false));
    }

    Ok(LoadedDoc {
        doc,
        pdf_options,
        fontdb,
        page_count,
    })
}

fn ensure_parent_dir(out_path: &Path) -> Result<(), String> {
    if let Some(parent) = out_path.parent() {
        if !parent.as_os_str().is_empty() {
            fs::create_dir_all(parent).map_err(|e| format!("mkdir failed: {e}"))?;
        }
    }
    Ok(())
}

fn render_page_to_pdf(loaded: &LoadedDoc, pages: &[u32], out_path: &Path) -> Result<usize, String> {
    ensure_parent_dir(out_path)?;
    let bytes = loaded
        .doc
        .render_pages_pdf_native_with_profile_and_options(pages, RenderProfile::Print, &loaded.pdf_options)
        .map_err(|e| e.to_string())?;
    fs::write(out_path, &bytes).map_err(|e| format!("write failed: {e}"))?;
    Ok(bytes.len())
}

fn render_page_to_png(
    loaded: &LoadedDoc,
    page: u32,
    scale: f32,
    out_path: &Path,
) -> Result<(usize, u32, u32), String> {
    ensure_parent_dir(out_path)?;
    let svg_raw = loaded
        .doc
        .render_page_svg_layer_with_profile_native(page, RenderProfile::Print)
        .map_err(|e| e.to_string())?;
    let svg = apply_pdf_font_options(&svg_raw, &loaded.pdf_options);

    let options = usvg::Options {
        resources_dir: None,
        fontdb: Arc::clone(&loaded.fontdb),
        image_href_resolver: svg_image_href_resolver(),
        ..Default::default()
    };

    let tree = usvg::Tree::from_str(&svg, &options).map_err(|e| format!("svg parse failed: {e}"))?;
    let size = tree.size();
    let base_w = size.width().max(1.0);
    let base_h = size.height().max(1.0);

    let clamped_scale = scale.clamp(0.25, 4.0);
    let max_fit_scale = (MAX_RASTER_DIM as f32 / base_w).min(MAX_RASTER_DIM as f32 / base_h);
    let effective_scale = clamped_scale.min(max_fit_scale).max(0.1);

    let px_w = ((base_w * effective_scale).round() as u32).clamp(1, MAX_RASTER_DIM);
    let px_h = ((base_h * effective_scale).round() as u32).clamp(1, MAX_RASTER_DIM);

    let mut pixmap = tiny_skia::Pixmap::new(px_w, px_h)
        .ok_or_else(|| "failed to allocate pixmap".to_string())?;
    pixmap.fill(tiny_skia::Color::WHITE);

    let transform = tiny_skia::Transform::from_scale(px_w as f32 / base_w, px_h as f32 / base_h);
    resvg::render(&tree, transform, &mut pixmap.as_mut());

    let png = pixmap.encode_png().map_err(|e| format!("png encode failed: {e}"))?;
    fs::write(out_path, &png).map_err(|e| format!("write failed: {e}"))?;
    Ok((png.len(), px_w, px_h))
}

#[derive(Deserialize)]
#[serde(tag = "op", rename_all = "snake_case")]
enum WorkerReq {
    Open { path: String },
    Page {
        page: u32,
        out: String,
        #[serde(default)]
        scale: Option<f32>,
    },
    Quit,
}

fn run_worker() -> i32 {
    let stdin = io::stdin();
    let mut stdout = io::stdout();
    let mut current: Option<LoadedDoc> = None;

    for line in stdin.lock().lines() {
        let Ok(line) = line else {
            break;
        };
        let trimmed = line.trim();
        if trimmed.is_empty() {
            continue;
        }
        let resp = match serde_json::from_str::<WorkerReq>(trimmed) {
            Ok(WorkerReq::Open { path }) => match load_for_preview(Path::new(&path)) {
                Ok(loaded) => {
                    let pages = loaded.page_count;
                    current = Some(loaded);
                    serde_json::json!({ "ok": true, "pages": pages })
                }
                Err((err, encrypted)) => {
                    current = None;
                    serde_json::json!({ "ok": false, "error": err, "encrypted": encrypted })
                }
            },
            Ok(WorkerReq::Page { page, out, scale }) => match &current {
                Some(loaded) => {
                    if page >= loaded.page_count {
                        serde_json::json!({ "ok": false, "error": "page out of range" })
                    } else if out.to_ascii_lowercase().ends_with(".pdf") {
                        match render_page_to_pdf(loaded, &[page], Path::new(&out)) {
                            Ok(bytes) => serde_json::json!({ "ok": true, "bytes": bytes }),
                            Err(e) => serde_json::json!({ "ok": false, "error": e }),
                        }
                    } else {
                        let s = scale.unwrap_or(DEFAULT_PNG_SCALE);
                        match render_page_to_png(loaded, page, s, Path::new(&out)) {
                            Ok((bytes, width, height)) => serde_json::json!({
                                "ok": true,
                                "bytes": bytes,
                                "width": width,
                                "height": height,
                            }),
                            Err(e) => serde_json::json!({ "ok": false, "error": e }),
                        }
                    }
                }
                None => serde_json::json!({ "ok": false, "error": "no document open" }),
            },
            Ok(WorkerReq::Quit) => {
                let _ = writeln!(stdout, "{{\"ok\":true}}");
                let _ = stdout.flush();
                return 0;
            }
            Err(e) => serde_json::json!({ "ok": false, "error": format!("bad request: {e}") }),
        };
        if writeln!(stdout, "{}", resp).is_err() || stdout.flush().is_err() {
            break;
        }
    }
    0
}

fn run_export_png(args: &[String]) -> i32 {
    let mut input: Option<String> = None;
    let mut output: Option<String> = None;
    let mut page: u32 = 0;
    let mut scale: f32 = DEFAULT_PNG_SCALE;
    let mut json = false;
    let mut i = 0;
    while i < args.len() {
        match args[i].as_str() {
            "-o" | "--output" => {
                i += 1;
                if i >= args.len() {
                    eprintln!("missing value for -o");
                    return 2;
                }
                output = Some(args[i].clone());
            }
            "-p" | "--page" => {
                i += 1;
                if i >= args.len() {
                    eprintln!("missing value for -p");
                    return 2;
                }
                match args[i].parse::<u32>() {
                    Ok(p) => page = p,
                    Err(_) => {
                        eprintln!("invalid page number");
                        return 2;
                    }
                }
            }
            "--scale" => {
                i += 1;
                if i >= args.len() {
                    eprintln!("missing value for --scale");
                    return 2;
                }
                match args[i].parse::<f32>() {
                    Ok(s) if s.is_finite() && s > 0.0 => scale = s,
                    _ => {
                        eprintln!("invalid scale");
                        return 2;
                    }
                }
            }
            "--json" => json = true,
            arg if arg.starts_with('-') => {
                eprintln!("unknown option: {arg}");
                return 2;
            }
            arg => {
                if input.is_some() {
                    eprintln!("unexpected argument: {arg}");
                    return 2;
                }
                input = Some(arg.to_string());
            }
        }
        i += 1;
    }

    let Some(input_path) = input else {
        eprintln!("missing input file");
        return 2;
    };
    let out_path = output.unwrap_or_else(|| "output.png".to_string());

    let loaded = match load_for_preview(Path::new(&input_path)) {
        Ok(l) => l,
        Err((err, _)) => {
            eprintln!("{err}");
            return 1;
        }
    };

    if page >= loaded.page_count {
        eprintln!("page {page} out of range (0..{})", loaded.page_count - 1);
        return 2;
    }

    match render_page_to_png(&loaded, page, scale, Path::new(&out_path)) {
        Ok((bytes, width, height)) => {
            if json {
                println!(
                    "{}",
                    serde_json::json!({
                        "backend": "resvg",
                        "output": out_path,
                        "bytes": bytes,
                        "width": width,
                        "height": height,
                        "pageCount": loaded.page_count,
                        "page": page,
                    })
                );
            }
            0
        }
        Err(e) => {
            eprintln!("{e}");
            1
        }
    }
}

fn run_export_pdf(args: &[String]) -> i32 {
    let mut input: Option<String> = None;
    let mut output: Option<String> = None;
    let mut page: Option<u32> = None;
    let mut json = false;
    let mut i = 0;
    while i < args.len() {
        match args[i].as_str() {
            "-o" | "--output" => {
                i += 1;
                if i >= args.len() {
                    eprintln!("missing value for -o");
                    return 2;
                }
                output = Some(args[i].clone());
            }
            "-p" | "--page" => {
                i += 1;
                if i >= args.len() {
                    eprintln!("missing value for -p");
                    return 2;
                }
                match args[i].parse::<u32>() {
                    Ok(p) => page = Some(p),
                    Err(_) => {
                        eprintln!("invalid page number");
                        return 2;
                    }
                }
            }
            "--json" => json = true,
            arg if arg.starts_with('-') => {
                eprintln!("unknown option: {arg}");
                return 2;
            }
            arg => {
                if input.is_some() {
                    eprintln!("unexpected argument: {arg}");
                    return 2;
                }
                input = Some(arg.to_string());
            }
        }
        i += 1;
    }

    let Some(input_path) = input else {
        eprintln!("missing input file");
        return 2;
    };
    let out_path = output.unwrap_or_else(|| "output.pdf".to_string());

    let loaded = match load_for_preview(Path::new(&input_path)) {
        Ok(l) => l,
        Err((err, _)) => {
            eprintln!("{err}");
            return 1;
        }
    };

    let pages: Vec<u32> = match page {
        Some(p) => {
            if p >= loaded.page_count {
                eprintln!("page {p} out of range (0..{})", loaded.page_count - 1);
                return 2;
            }
            vec![p]
        }
        None => (0..loaded.page_count).collect(),
    };

    match render_page_to_pdf(&loaded, &pages, Path::new(&out_path)) {
        Ok(bytes) => {
            if json {
                println!(
                    "{}",
                    serde_json::json!({
                        "backend": "svg",
                        "output": out_path,
                        "bytes": bytes,
                        "pageCount": loaded.page_count,
                        "renderedCount": pages.len(),
                    })
                );
            }
            0
        }
        Err(e) => {
            eprintln!("{e}");
            1
        }
    }
}

fn main() {
    let args: Vec<String> = env::args().collect();
    let code = match args.get(1).map(|s| s.as_str()) {
        Some("--version") | Some("-V") => {
            println!("rhwp-preview 0.1.0 (rhwp {})", rhwp::version());
            0
        }
        Some("--help") | Some("-h") | None => {
            println!("rhwp-preview: minimal HWP/HWPX preview renderer for M-Dir");
            println!("  rhwp-preview export-png <file> [-p N] [--scale S] [-o out.png] [--json]");
            println!("  rhwp-preview export-pdf <file> [-p N] [-o out.pdf] [--json]");
            println!("  rhwp-preview --worker");
            0
        }
        Some("--worker") => run_worker(),
        Some("export-png") => run_export_png(&args[2..]),
        Some("export-pdf") => run_export_pdf(&args[2..]),
        Some(other) => {
            eprintln!("unknown command: {other}");
            2
        }
    };
    process::exit(code);
}
