#!/usr/bin/python3
"""Update Word document indexes through an already-running LibreOffice UNO listener."""

import json
import pathlib
import re
import sys
import time

import uno
from com.sun.star.beans import PropertyValue


def property_value(name, value):
    item = PropertyValue()
    item.Name = name
    item.Value = value
    return item


def connect(port):
    local_context = uno.getComponentContext()
    resolver = local_context.ServiceManager.createInstanceWithContext(
        "com.sun.star.bridge.UnoUrlResolver", local_context
    )
    target = f"uno:socket,host=127.0.0.1,port={port};urp;StarOffice.ComponentContext"
    last_error = None
    for _ in range(100):
        try:
            return resolver.resolve(target)
        except Exception as error:
            last_error = error
            time.sleep(0.1)
    raise RuntimeError(f"UNO listener was not reachable: {last_error}")


def normalize_text(value):
    return " ".join(value.split()).strip()


def document_headings(document):
    headings = []
    paragraphs = document.Text.createEnumeration()
    while paragraphs.hasMoreElements():
        paragraph = paragraphs.nextElement()
        style = str(getattr(paragraph, "ParaStyleName", ""))
        if not re.fullmatch(r"Heading\s*[1-4]", style, flags=re.IGNORECASE):
            continue
        text = normalize_text(paragraph.getString())
        if text and text not in headings:
            headings.append(text)
    return headings


def inspect_indexes(document):
    indexes = document.getDocumentIndexes()
    rendered_lines = []
    for index in range(indexes.getCount()):
        anchor = indexes.getByIndex(index).getAnchor()
        rendered_text = anchor.getString() if anchor is not None else ""
        rendered_lines.extend(
            normalize_text(line) for line in rendered_text.splitlines() if normalize_text(line)
        )

    headings = document_headings(document)
    matched_headings = sum(
        1 for heading in headings if any(heading in line for line in rendered_lines)
    )
    page_values = []
    for line in rendered_lines:
        match = re.search(r"(?:\t|\.{2,}|\s)(\d+)\s*$", line)
        if match is not None:
            page_values.append(int(match.group(1)))
    return headings, rendered_lines, matched_headings, page_values


def refresh_indexes(desktop, input_url):
    document = desktop.loadComponentFromURL(
        input_url,
        "_blank",
        0,
        (
            property_value("Hidden", True),
            property_value("ReadOnly", False),
            property_value("UpdateDocMode", 3),
        ),
    )
    if document is None:
        raise RuntimeError("LibreOffice could not load the DOCX preview copy")

    try:
        indexes = document.getDocumentIndexes()
        index_count = indexes.getCount()
        updated_count = 0
        for index in range(index_count):
            document_index = indexes.getByIndex(index)
            if hasattr(document_index, "refresh"):
                document_index.refresh()
            else:
                document_index.update()
            updated_count += 1
        document.store()
        headings, lines, matched_heading_count, page_values = inspect_indexes(document)
        return (
            index_count,
            updated_count,
            headings,
            lines,
            matched_heading_count,
            page_values,
        )
    finally:
        document.close(True)


def main():
    if len(sys.argv) != 4:
        raise RuntimeError("usage: update-word-indexes.py PORT INPUT_DOCX OUTPUT_PDF")

    port = int(sys.argv[1])
    input_path = pathlib.Path(sys.argv[2]).resolve(strict=True)
    output_path = pathlib.Path(sys.argv[3]).resolve()
    remote_context = connect(port)
    service_manager = remote_context.ServiceManager
    desktop = service_manager.createInstanceWithContext("com.sun.star.frame.Desktop", remote_context)
    input_url = uno.systemPathToFileUrl(str(input_path))
    index_count = 0
    updated_count = 0
    update_pass_count = 0
    index_converged = False
    previous_signature = None
    for update_pass_count in range(1, 4):
        (
            index_count,
            updated_count,
            _,
            lines,
            _,
            _,
        ) = refresh_indexes(desktop, input_url)
        signature = tuple(lines)
        if signature == previous_signature:
            index_converged = True
            break
        previous_signature = signature

    verification = desktop.loadComponentFromURL(
        input_url,
        "_blank",
        0,
        (
            property_value("Hidden", True),
            property_value("ReadOnly", True),
            property_value("UpdateDocMode", 0),
        ),
    )
    if verification is None:
        raise RuntimeError("LibreOffice could not reopen the updated DOCX preview copy")
    try:
        headings, lines, matched_heading_count, page_values = inspect_indexes(verification)
        index_converged = index_converged and tuple(lines) == previous_signature
        verification.storeToURL(
            uno.systemPathToFileUrl(str(output_path)),
            (
                property_value("FilterName", "writer_pdf_Export"),
                property_value("Overwrite", True),
            ),
        )
    finally:
        verification.close(True)

    if not output_path.is_file() or output_path.stat().st_size == 0:
        raise RuntimeError("LibreOffice did not create the requested PDF")
    print(
        json.dumps(
            {
                "index_count": index_count,
                "updated_count": updated_count,
                "update_pass_count": update_pass_count,
                "index_converged": index_converged,
                "entry_line_count": len(lines),
                "page_number_count": len(page_values),
                "max_page_number": max(page_values, default=0),
                "expected_heading_count": len(headings),
                "matched_heading_count": matched_heading_count,
            }
        )
    )


if __name__ == "__main__":
    main()
