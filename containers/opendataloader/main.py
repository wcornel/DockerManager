import os
import shutil
import tempfile
from pathlib import Path
from fastapi import FastAPI, File, UploadFile, HTTPException, Query
from fastapi.responses import JSONResponse
import opendataloader_pdf

app = FastAPI(
    title="OpenDataLoader PDF Service",
    version="1.0.0",
    description="REST API voor het converteren en parsen van PDF documenten naar AI-ready Markdown, JSON en HTML."
)

@app.get("/")
def root():
    return {
        "service": "OpenDataLoader PDF API",
        "status": "running",
        "docs_url": "/docs"
    }

@app.get("/health")
def health_check():
    return {"status": "healthy"}

@app.post("/convert")
async def convert_pdf(
    file: UploadFile = File(...),
    format: str = Query("markdown,json", description="Kommagescheiden formaten: markdown, json, html, pdf")
):
    """
    Upload een PDF bestand en ontvang de geëxtraheerde tekst en gestructureerde data.
    """
    if not file.filename.lower().endswith(".pdf"):
        raise HTTPException(status_code=400, detail="Alleen PDF bestanden worden ondersteund.")

    with tempfile.TemporaryDirectory() as tmp_dir:
        input_file_path = os.path.join(tmp_dir, file.filename)
        output_dir = os.path.join(tmp_dir, "output")
        os.makedirs(output_dir, exist_ok=True)

        # Sla geüploade bestand op
        with open(input_file_path, "wb") as f:
            content = await file.read()
            f.write(content)

        try:
            # Voer OpenDataLoader conversie uit
            opendataloader_pdf.convert(
                input_path=[input_file_path],
                output_dir=output_dir,
                format=format
            )

            # Verzamel gegenereerde output bestanden
            results = {}
            for out_file in Path(output_dir).rglob("*"):
                if out_file.is_file():
                    ext = out_file.suffix.lower().lstrip(".")
                    try:
                        content_text = out_file.read_text(encoding="utf-8")
                        results[out_file.name] = content_text
                    except Exception:
                        results[out_file.name] = f"[Binaire data - {out_file.stat().st_size} bytes]"

            return {
                "success": True,
                "filename": file.filename,
                "formats": format.split(","),
                "results": results
            }

        except Exception as e:
            raise HTTPException(status_code=500, detail=f"Fout bij PDF conversie: {str(e)}")

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8080)
