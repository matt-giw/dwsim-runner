# Flowsheet templates

Place canonical `.dwxmz` flowsheets here, one per reference plant, e.g.:

    pem-ref-plant.dwxmz    # the ~5 MW PEM reference plant

The /solve API parameterizes these via overrides rather than building
flowsheets programmatically — keep object Tags stable ("feed water", etc.)
since overrides address objects by Tag.
